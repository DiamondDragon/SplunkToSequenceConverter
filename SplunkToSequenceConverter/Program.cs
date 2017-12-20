using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace SplunkToSequenceConverter
{
    class Program
    {
        private static readonly bool PrintThreadId = true;
        private static readonly bool UseActualLogicExecutionFlow = false;

        private const string PfpWebsite = "pfp";

        private static readonly Regex PpfResponsePattern =
            new Regex(
                @"Calling API at\s*(?<url>.+)?\s*with verb\s*(?<method>\w+)\s*gave status code\s*(?<status>\w+)\s*and took a time of\s*(?<duration>\d+)\s*ms",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

        private static readonly Regex PfpRequestPattern =
            new Regex(
                @"About to call API at\s*(?<url>.+)\s*with verb\s*(?<method>\w+)",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

        private static readonly HashSet<string> AllowedLoggers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ApiHttpClient",
            "CacheForRequestHandler"
        };

        static void Main(string[] args)
        {

            if (args.Length != 2)
            {
                Console.WriteLine("Splunk query: index=* a_rid=<guid> AND (a_logger=ApiHttpClient OR a_logger=CacheForRequestHandler)");
                Console.WriteLine("Usage:");
                Console.WriteLine("SplunkToSequenceConverter.exe <logFile> <outputFile>");
                return;
            }

            var logFile = args[0];
            var dstFile = args[1];

            var tokens =
                (from item in File.ReadAllLines(logFile)
                 let token = JToken.Parse(item)["result"]
                 where IsKnownLogEntry(token)
                 orderby (string)token["a_time"] descending 
                 select token)
                .ToArray();

            var activities = new List<Activity>();

            for (var i = 0; i < tokens.Length; i++)
            {
                var linedata = tokens[i];

                if (IsSupported(linedata))
                    activities.AddRange(CreateActivity(linedata, i, tokens));
            }

            activities.Reverse();

            var builder = new StringBuilder();

            builder.AppendLine("@startuml");
            builder.AppendLine();
            builder.AppendLine("title PFP collaboration");
            builder.AppendLine();

            foreach (var activity in activities)
            {
                builder.AppendLine($"{activity.From} -> {activity.To}: {activity.Message}");
            }

            builder.AppendLine();
            builder.Append("@enduml");

            File.WriteAllText(dstFile, builder.ToString());
        }

        private static bool IsSupported(JToken lineData)
        {
            if (lineData["API_TIMETAKENMS"] != null)
                return false;

            return true;
        }

        private static bool IsKnownLogEntry(JToken token)
        {
            var logger = (string)token["a_logger"];

            if (string.IsNullOrEmpty(logger))
                return false;

            if (!AllowedLoggers.Contains(logger))
                return false;

            if (token["API_TIMETAKENMS"] != null)
                return false;

            return true;
        }


        private static IEnumerable<Activity> CreateActivity(JToken token, int lineIndex, JToken[] lines)
        {
            var message = (string) token["a_msg"];

            if (PpfResponsePattern.IsMatch(message))
            {
                var results = CreatePfpResponseActivity(message, token, lineIndex, lines).ToArray();

                yield return results[0];

                if (UseActualLogicExecutionFlow)
                    yield return results[1];
            }


            if (!UseActualLogicExecutionFlow && PfpRequestPattern.IsMatch(message))
                yield return CreatePfpRequestActivity(message, token);


            if (token["RESULTS_CACHED"] != null)
                yield return CreateCachedRequestAction(token);
        }

        private static Activity CreateCachedRequestAction(JToken token)
        {
            var url = (string)token["API_CALL"];
            var method = (string)token["METHOD"];
            var threadId = GetThreadId(token);

            var info = ParseRequestInfo(url, method);

            return new Activity
            {
                From = PfpWebsite,
                To = PfpWebsite,
                Message = $"T:{threadId}, Found in cache: {info.ServiceName} {method.ToUpper()} {info.Url}"
            };
        }

        private static string GetThreadId(JToken token)
        {
            return PrintThreadId? (string)token["a_thread"] : string.Empty;
        }

        private static Activity CreatePfpRequestActivity(string message, JToken token)
        {
            var match = PfpRequestPattern.Match(message);

            var threadId = GetThreadId(token);
            var method = match.Groups["method"].Value;
            var url = match.Groups["url"].Value;

            var info = ParseRequestInfo(url, method);

            return new Activity
            {
                From = PfpWebsite,
                To = info.ServiceName,
                Message = $"T:{threadId}, {method.ToUpper()} {info.Url}"
            };
        }

        private static IEnumerable<Activity> CreatePfpResponseActivity(string message, JToken token, int lineIndex, JToken[] lines)
        {
            var match = PpfResponsePattern.Match(message);

            var url = match.Groups["url"].Value;
            var duration = match.Groups["duration"].Value;
            var status = match.Groups["status"].Value;
            var threadId = GetThreadId(token);

            var requestToken = FindRequest(url, lineIndex + 1, lines);
            var requestMessage = (string)requestToken["a_msg"];
            var requestMatch = PfpRequestPattern.Match(requestMessage);
            var request = ParseRequestInfo(requestMatch.Groups["url"].Value, requestMatch.Groups["method"].Value);

            var activitymessage = UseActualLogicExecutionFlow
                ? $"T:{threadId}, HTTP 1.1 {(int) Enum.Parse(typeof(HttpStatusCode), status, true)} ({status})"
                : $"T:{threadId}, HTTP 1.1 {(int) Enum.Parse(typeof(HttpStatusCode), status, true)} ({status}), {request.ServiceName} {request.Method.ToUpper()} {request.Url}";

            yield return new Activity
            {
                From = request.ServiceName,
                Message = activitymessage,
                To = PfpWebsite
            };

            yield return CreatePfpRequestActivity(requestMessage, requestToken);
        }

        private static JToken FindRequest(string url, int startIndex, JToken[] lines)
        {
            for (var i = startIndex; i < lines.Length; i++)
            {
                var message = (string) lines[i]["a_msg"];

                var match = PfpRequestPattern.Match(message);
                if (!match.Success)
                    continue;

                if (!match.Groups["url"].Value.ToLower().Contains(url.ToLower()))
                    continue;

                return lines[i];
            }

            return null;
        }

        private static RequestInfo ParseRequestInfo(string url, string method)
        {
            url = url.Trim('"', '\'');

            var uri = new Uri(url, UriKind.Absolute);

            return new RequestInfo
            {
                Method = method,
                ServiceName = uri.Host.Substring(0, uri.Host.Length - ".intelliflo.com".Length),
                Url = uri.PathAndQuery
            };
        }
    }

    public class RequestInfo
    {
        public string ServiceName { get; set; }
        public string Url { get; set; }
        public string Method { get; set; }
    }

    public class Activity
    {
        public string From { get; set; }
        public string To { get; set; }
        public string Message { get; set; }
    }
}

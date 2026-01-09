
using System.Security.AccessControl;
using System.Text;

namespace LyWaf.Utils
{

    public static class RequestUtil
    {

        public static string GetClientIp(HttpRequest request)
        {
            if (request.Headers.TryGetValue("X-Forwarded-For", out var val))
            {
                var s = val.ToString().Split(",");
                return s[0];
            }

            if (request.Headers.TryGetValue("X-Real-IP", out val))
            {
                return val.ToString();
            }

            return request.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";
        }

        public static string RecordRuquest(HttpRequest request)
        {
            return RecordRuquest("win-curl", request, null);
        }

        public static string RecordRuquest(HttpRequest request, string? body = null)
        {
            return RecordRuquest("win-curl", request, body);
        }

        public static string RecordRuquest(string methd, HttpRequest request, string? body = null)
        {
            StringBuilder sb = new();
            sb.Append("curl ");
            sb.Append("'" + GetRequestUrl(request) + "' ");
            if (request.Method != "GET")
            {
                sb.Append("\\\n-X " + request.Method + " ");
            }
            foreach (var header in request.Headers)
            {
                sb.Append(string.Format("\\\n-H '{0}:{1}' ", header.Key, header.Value));
            }
            if (body != null && body.Length > 0)
            {
                sb.Append("\\\n--data-raw '" + body + "'");
            }
            return sb.ToString();
        }

        public static string GetRequestUrl(HttpRequest request)
        {
            return string.Format("{0}://{1}{2}", request.Scheme, request.Host, request.Path);
        }

        public static string GetRequestBaseUrl(HttpRequest request)
        {
            return string.Format("{0}://{1}", request.Scheme, request.Host);
        }

    }
}
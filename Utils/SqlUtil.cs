
using System.Text;

namespace LyWaf.Utils;

public class SqlUtil {

    public static string Unescape(string val) {
        var index = val.IndexOf("/*", 0);
        if(index != -1) {
            StringBuilder sb = new();
            var startIdx = 0;
            while(true) {
                sb.Append(val[startIdx..index]);
                var nextIdx = val.IndexOf("*/", index);
                if(nextIdx == -1) {
                    break;
                }
                startIdx = nextIdx + 2;
                index = val.IndexOf("/*", startIdx);
                if(index == -1) {
                    sb.Append(val[startIdx..]);
                    break;
                }
            }
            val = sb.ToString();
        }
        if(val.Contains('\n')) {
            return val.Replace('\n', ' ');
        }
        return val;
    }
}

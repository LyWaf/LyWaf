
using System.Text;
using Xunit;

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

    [Fact]
    public void TestName()
    {
        Assert.Equal("union", Unescape("un/**/ion"));
        Assert.Equal("union", Unescape("un/*aa*/ion"));
        Assert.Equal("union select", Unescape("un/*aa*/ion select"));
        Assert.Equal("union select", Unescape("un/*aa*/ion sel/**/ect"));
        Assert.Equal("union select", Unescape("un/*aa*/ion sel/*select*/ect"));
        Assert.Equal("union select", Unescape("un/*aa*/ion\nsel/*select*/ect"));
    }
}
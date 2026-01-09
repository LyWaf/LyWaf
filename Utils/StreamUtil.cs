using System.Security.AccessControl;
using System.Text;

namespace LyWaf.Utils
{
    public static class StreamUtil
    {

        public static string ConvertToString(this Stream stream, Encoding? encoding = null)
        {
            ArgumentNullException.ThrowIfNull(stream);

            encoding ??= Encoding.UTF8;

            // 如果流支持查找（如 MemoryStream），重置位置
            if (stream.CanSeek)
                stream.Position = 0;

            using StreamReader reader = new(stream, encoding);
            return reader.ReadToEnd();
        }

        public static async Task<string> ConvertToStringAsync(this Stream stream, Encoding? encoding = null)
        {

            ArgumentNullException.ThrowIfNull(stream);

            encoding ??= Encoding.UTF8;

            // 如果流支持查找（如 MemoryStream），重置位置
            if (stream.CanSeek)
                stream.Position = 0;

            using StreamReader reader = new(stream, encoding);
            return await reader.ReadToEndAsync();
        }
    }
}
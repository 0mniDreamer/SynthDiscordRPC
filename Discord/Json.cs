using System.Text;

namespace SynthDiscordRPC.Discord
{

    /// Minimal JSON string escaping for the small payloads we write to Discord.
    /// We only ever WRITE JSON (handshake + SET_ACTIVITY); responses are consumed
    /// and discarded, so no parser is needed. Zero dependencies by design.
 
    internal static class Json
    {
        ///Escape a string and wrap it in quotes. Null returns the literal null.
        public static string Str(string s)
        {
            if (s == null) return "null";

            var sb = new StringBuilder(s.Length + 8);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}

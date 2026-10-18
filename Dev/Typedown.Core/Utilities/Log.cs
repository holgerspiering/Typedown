using System.Threading.Tasks;

namespace Typedown.Core.Utilities
{
    public static class Log
    {
        // Crash reports went to the original author's server; disabled in this fork
        public static Task Report(string type, string content)
        {
            return Task.CompletedTask;
        }
    }
}

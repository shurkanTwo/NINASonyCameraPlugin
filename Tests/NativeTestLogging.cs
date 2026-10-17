// The portable suite compiles the native wrappers directly. Only NINA logging
// and notifications are replaced; all camera control and error handling is real.
namespace NINA.Core.Utility {
    public static class Logger {
        public static void Error(string message, System.Exception error) { }
        public static void Warning(string message) { }
        public static void Info(string message) { }
    }
}

namespace NINA.Core.Utility.Notification {
    public static class Notification {
        public static void ShowError(string message) { }
    }
}

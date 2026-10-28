using System;

namespace AIBridge.Runtime
{
    /// <summary>为 Runtime 命令提取异常信息，避免反射堆栈失败中断响应。</summary>
    internal static class RuntimeExceptionDetails
    {
        internal static string GetStackTrace(Exception exception)
        {
            try
            {
                return exception?.StackTrace;
            }
            catch (Exception stackTraceError)
            {
                return "Stack trace unavailable (" + stackTraceError.GetType().FullName + ").";
            }
        }
    }
}

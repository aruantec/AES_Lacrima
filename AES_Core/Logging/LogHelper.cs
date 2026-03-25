using System;
using log4net;

namespace AES_Core.Logging;

public static class LogHelper
{
    public static ILog For<T>() => For(typeof(T));

    public static ILog For(Type type)
    {
        string loggerName = type.FullName ?? type.Name;
        return LogManager.GetLogger(type.Assembly, loggerName);
    }
}

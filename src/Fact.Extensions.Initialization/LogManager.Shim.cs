using System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace Fact.Extensions
{
    // FIX: Super sloppy.  Perhaps we need to formalize this shim and bring back
    // LogManager for limited static class application?
    public static class LogManager
    {
        static IServiceProvider provider;

        /// <summary>
        /// Retrieve a logger via embedded provider
        /// </summary>
        /// <returns></returns>
        public static ILogger CreateLogger<T>()
        {
            return provider.GetService<ILoggerFactory>().CreateLogger<T>();
        }

        /// <summary>
        /// Retrieve a logger via embedded provider
        /// </summary>
        /// <returns></returns>
        public static ILogger GetLogger(string named)
        {
            return provider.GetService<ILoggerFactory>().CreateLogger(named);
        }

        static void Initializer(IServiceProvider provider)
        {
            LogManager.provider = provider;
        }
    }
}
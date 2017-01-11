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

        public static void Initializer(IServiceProvider provider)
        {
            LogManager.provider = provider;
        }
    }
}


// This appears out in Fact.Extensions.Collection, 
// temporarily reimplementing it here
namespace Fact.Extensions.Collection
{
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;

    public static class StringEnumerationExtensions
    {
        /// <summary>
        /// Note that null strings don't get included
        /// </summary>
        /// <param name="delim"></param>
        /// <param name="s"></param>
        /// <returns></returns>
        internal static string Concat(string delim, IEnumerable<string> s)
        {
            string result = null;

            foreach (var _s in s)
            {
                if (!string.IsNullOrEmpty(_s))
                {
                    if (result != null)
                        result += delim + _s;
                    else
                        result = _s;
                }
            }

            return result;
        }

#if NETSTANDARD1_3 || NETSTANDARD1_6
        /// <summary>
        /// Converts the given enumeration to a string, each item separated by a delimiter.  Be mindful 
        /// empty strings don't get included
        /// </summary>
        /// <returns>
        /// Concat'd string OR null if enumerable was empty
        /// </returns>
        public static string ToString(this IEnumerable enumerable, string delim)
        {
            return Concat(delim, enumerable.Cast<object>().Select(x => x.ToString()));
        }

        /*
        /// <summary>
        /// Converts the given enumeration to a string, each item separated by a delimiter.  Be mindful 
        /// empty strings don't get included
        /// </summary>
        /// <returns>
        /// Concat'd string OR null if enumerable was empty
        /// </returns>
        public static string ToString(this object[] enumerable, string delim)
        {
            return Concat(delim, enumerable.Select(x => x.ToString()));
        }*/
#endif


        /// <summary>
        /// Converts the string enumeration to one string, each item separated by the given delimeter
        /// </summary>
        /// <param name="This"></param>
        /// <param name="delim"></param>
        /// <returns>If enumeration is empty, NULL is returned</returns>
        public static string ToString(this IEnumerable<string> This, string delim)
        {
            return Concat(delim, This);
        }
    }
}

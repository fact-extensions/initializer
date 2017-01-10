
using System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace Fact.Extensions.Initialization
{
#if !FEATURE_POLICY
    public interface IExceptionPolicy
    {
        void HandleException(Exception e);
    }
    public class ExceptionPolicy
    {
        public void HandleException(Exception e)
        {
            throw e;
        }
    }
#endif
}
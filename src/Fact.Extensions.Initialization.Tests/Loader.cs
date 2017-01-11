using Fact.Extensions.Initialization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

namespace Fact._Global
{
    /// <summary>
    /// Dummy initializer in crusty old namespace paradigm, just to get things online
    /// and tested
    /// </summary>
    public class Loader : ILoader
    {
        public void Initialize()
        {
            var logger = Fact.Extensions.LogManager.CreateLogger<Loader>();

            logger.LogInformation("Got to initializer");
        }
    }
}

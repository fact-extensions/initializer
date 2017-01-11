using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyModel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace Fact.Extensions.Initialization.Tests
{
    [TestClass]
    public class BasicTests
    {
        [TestMethod]
        public void Basic1Test()
        {
            GetReferencingAssemblies("");
        }

        [TestMethod]
        public void DependencyWalkerTest()
        {
            var dependencies = DependencyContext.Default.RuntimeLibraries;
            var __assemblies = dependencies.ToArray();
            var _assemblies = dependencies.Select(x =>
            {
                try
                {
                    return Assembly.Load(new AssemblyName(x.Name));
                }
                catch(Exception e)
                {
                    return null;
                }
                }
                    );

            // VERY kludgey, only doing this in the meantime until AssemblyDependencyBuilder takes
            // RuntimeLibrary instead of Assembly as its input
            var assemblies = _assemblies.Where(x => x != null);
            var assembliesTask = Task.FromResult(assemblies);
            AssemblyDependencyBuilder b = new AssemblyDependencyBuilder(assembliesTask);

            //var entryAssembly = Assembly.GetEntryAssembly();
            var entryAssembly = assemblies.First(x => x.FullName.StartsWith("Fact.Extensions.Initialization.Tests,"));
            b.Dig(entryAssembly, null);
        }

        [AssemblyInitialize]
        static public void Init(TestContext context)
        {
            var sc = new ServiceCollection();

            sc.AddLogging();

            var sp = sc.BuildServiceProvider();
            LogManager.Initializer(sp);
        }

        public static IEnumerable<Assembly> GetReferencingAssemblies(string assemblyName)
        {
            var assemblies = new List<Assembly>();
            var dependencies = DependencyContext.Default.RuntimeLibraries;
            foreach (var library in dependencies)
            {
                if (IsCandidateLibrary(library, assemblyName))
                {
                    var assembly = Assembly.Load(new AssemblyName(library.Name));
                    assemblies.Add(assembly);
                }
            }
            return assemblies;
        }

        private static bool IsCandidateLibrary(RuntimeLibrary library, string assemblyName)
        {
            return library.Name == (assemblyName)
                || library.Dependencies.Any(d => d.Name.StartsWith(assemblyName));
        }
    }
}

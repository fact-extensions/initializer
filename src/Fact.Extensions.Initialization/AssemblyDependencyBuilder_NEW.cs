#define FEATURE_MULTILOADER

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using Fact.Extensions.Collection;
using System.Threading;

namespace Fact.Extensions.Initialization
{
    public class AssemblyDependencyBuilder_NEW :
        InitializingAsyncDependencyBuilder<Assembly>
    {
        static readonly ILogger logger = LogManager.CreateLogger<AssemblyDependencyBuilder>();
        static readonly ILogger loggerInit = LogManager.GetLogger("Initialization");
#if FEATURE_POLICY
        static readonly IPolicyProvider policyProvider = PolicyProvider.Get();
        static readonly ExceptionPolicy exceptionPolicy = policyProvider.GetExceptionPolicy();
#else
        static readonly ExceptionPolicy exceptionPolicy = new ExceptionPolicy();
#endif

        Task<IEnumerable<Assembly>> assemblyList;

        public AssemblyDependencyBuilder_NEW(Task<IEnumerable<Assembly>> assemblyList)
        {
            logger.LogDebug("Initializing " + nameof(AssemblyDependencyBuilder_NEW));
            this.assemblyList = assemblyList;
        }


        protected override DependencyBuilder<Assembly>.Node CreateNode(Assembly key)
        {
            return new Node(key, this);
        }

        /// <summary>
        /// First in last out buffer for shutdown
        /// </summary>
        LinkedList<Node> FILO = new LinkedList<Node>();

        /// <summary>
        /// 
        /// </summary>
        /// <remarks>
        /// TODO: Someday make this partial-async also
        /// (the same way post ILoaderAsync is async, not ILoaderAsync style itself) 
        /// Looks like we could do a Dig again, but this time fire things off immediately on create instead of
        /// on dig event.  For now roll with this old-style shutdown
        /// </remarks>
        public void Shutdown()
        {
            foreach (var item in FILO)
            {
#if FEATURE_MULTILOADER
                foreach (var loader in item.loaders.OfType<ILoaderShutdown>())
                {
#else
                {
                    loader = (ILoaderShutdown)item.loader;
#endif
                    loggerInit.LogInformation("Shutdown for assembly: " +
                        loader.GetType().GetTypeInfo().Assembly.FullName);
                    loader.Shutdown();
                }
            }
        }

        public new class Node : InitializingAsyncDependencyBuilder<Assembly>.Node
        {
            AssemblyDependencyBuilder_NEW parent;
#if FEATURE_MULTILOADER
            internal readonly IEnumerable<object> loaders;
#else
            internal readonly object loader;
#endif

            /// <summary>
            /// This item/assembly only should be dug into if it is either a rootnode OR it has loader code
            /// </summary>
            public override bool ShouldDig
            {
                get
                {
                    if (parent.rootNode == this)
                        return true;

                    if (ShouldInitialize || IsAsync)
                        return true;

                    return false;
                }
            }

            protected override bool ShouldInitialize
            {
                get
                {
#if FEATURE_MULTILOADER
                    // We already filter by ILoader but old flavor of AssemblyDependencyBuilder could actually handle
                    // standalone ILoaderAsync or ILoaderShutdown, so be explicit here in anticipation of bringing that functionality
                    // back
                    // TODO: Issue warning if multiple loaders are detected, suggest using only one because order of execution
                    // is undefined.  Multiple async is OK
                    var hasLoader = loaders.OfType<ILoader>().Any();
#else
                    var hasLoader = loader is ILoader;
#endif

                    loggerInit.LogInformation("Inspecting: " + Name + (hasLoader ? " (has loader)" : ""));

                    return hasLoader;
#if UNUSED
					if (!hasLoader)
					{
						// It's possible this is the root node and if we don't issue an initialization,
						// then the standard Completed event won't get fired, so be sure to do it here
                        // FIX: Define "we don't issue an initialization" I have forgotten what this means
						if (this == parent.rootNode)
						{
                        // FIX: it seems possible this block is entered inappropriately, it seems
                        // it shouldn't be entered if Initialize() got called.

                        // if Initilialize() got called, this wait call will immediately return
							WaitForAsyncInitializers();

                        // FIX: Stopgap, async completed gets fired off twice, once here
                        // and once in main Initialize() call 
                            if (!parent.IsAsyncInitialized)
                            {
                                parent.IsAsyncInitialized = true;

                                if (parent.AsyncCompleted != null)
                                    parent.AsyncCompleted();
                            }

							// since there's no actual initialization phase in this case, fire off completed right away

                        // FIX: This is firing parent.Completed twice.  Here is actually the first time,
                        // and elsewhere after EvalInitialize gets called a 2nd time, during MONO testing.  
                        // Even though it doesn't have a loader, EvalInitialize is picking it up (rootNode)
                        // this is a bug.
							// TODO: Need to fire off AsyncCompleted here also--
							if (parent.Completed != null)
								parent.Completed();
						}

						return false;
					}
					return true;
#endif
                }
            }


            protected override void WaitForDependencies()
            {
                // We have to wait until root Node has been "dug" completely, otherwise
                // not all async loaders may have had a chance to start
                // FIX: Appear we may have a bug where if NO loaders run at all, this never knows to ping
                // and get Set()
                parent.rootNodeEvh.WaitOne();

                // Wait for "phase 1" async initializers to complete.  Waiting at this point makes sense, because a 
                // full "dig" as we waited for above means all async initializers have kicked off
                // TODO: We can change this and wait only for IAsyncLoaders to complete which this particular
                // Node depends on, potentially unblocking initialization
                parent.WaitForAsyncInitializers();
            }

            protected override void Initialize()
            {
#if FEATURE_MULTILOADER
                foreach (var loader in loaders.OfType<ILoader>())
                {
#else
                {
#endif
                    try
                    {
                        ((ILoader)loader).Initialize();
                    }
                    catch (TypeLoadException tle)
                    {
                        loggerInit.LogError("Initializing: " + Name + " failed.  Could not load assembly because " + tle.TypeName + " could not be loaded");
                        throw new TypeLoadException("Could not initialize Assembly " + Name + " because "
                            + tle.TypeName + " could not be loaded", tle);
                    }
                    catch (Exception e)
                    {
                        // TODO: bring this back, useful to have the deep stack tracing
                        //loggerInit.ErrorWithInspection("Initializing: " + Name + " failed.", e);
                        loggerInit.LogError("Initializing: " + Name + " failed.", e);
                        throw new InvalidOperationException("Could not initialize: " + Name, e);
                    }
                }

                loggerInit.LogInformation("Initialized: " + Name);
            }

            public Node(Assembly key, AssemblyDependencyBuilder_NEW parent) : base(key)
            {
                this.parent = parent;

#if FEATURE_MULTILOADER
                //key.DefinedTypes.AsParallel(); // PLINQ mainly advantageous for expensive selectors, not necessarily
                // expensive initial enum producers
                // this "loaders" phase is a potential bottleneck
                var _loaders = key.DefinedTypes.Where(x => x.ImplementedInterfaces.Contains(typeof(ILoader)));
                loaders = _loaders.Select(x => Activator.CreateInstance(x.AsType())).ToArray();
#else
                loader = key.CreateInstance("Fact._Global.Loader");
#endif
                /*			
	                // If this isn't an init/shutdown participating assembly, add it to an exclude cache so that next
				// time we startup we don't bother with it
				if (!(loader is ILoader || loader is ILoaderAsync || loader is ILoaderShutdown))
				{

				}*/
#if FEATURE_MULTILOADER
                if (loaders.OfType<ILoaderShutdown>().Any())
#else
                if (loader is ILoaderShutdown)
#endif
                {
                    Initialized += item =>
                    {
                        lock (parent.FILO)
                        {
                            parent.FILO.AddFirst(this);
                        }
                    };
                }
            }

#if FEATURE_MULTILOADER
            public override bool IsAsync => loaders.OfType<ILoaderAsync>().Any();

            protected override void DoAsyncLoad()
            {
                foreach (var loader in loaders.OfType<ILoaderAsync>())
                    loader.Load();
            }
#else
            public override bool IsAsync => loader is ILoaderAsync;

            protected override void DoAsyncLoad()
            {
                ((ILoaderAsync)loader).Load();
            }
#endif


            public override string Name => value.GetName().Name;

            public override IEnumerable<Assembly> GetChildren()
            {
                return value.GetReferencedAssemblies().
                    Where(x => Initializer.SkipAssembly.FirstOrDefault(y => x.Name.StartsWith(y)) == null).
                    Where(x => !Initializer.Inspected.Contains(x.Name)).
                    Select(LoadAssembly);
            }

            Assembly LoadAssembly(AssemblyName name)
            {
                try
                {
                    return Assembly.Load(name);
                }
                catch (Exception e)
                {
                    throw new Exception("Failure while loading dependencies for: " + Name, e);
                }
            }
        }

        protected override object GetKey(Assembly key) => key.FullName;
    }
}

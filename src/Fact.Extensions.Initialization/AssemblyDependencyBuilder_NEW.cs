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
        InitializingDependencyBuilder<Assembly>
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
            this.assemblyList = assemblyList;
        }


        public bool IsAsyncInitialized { get; internal set; }

        /// <summary>
        /// Participate during Node construction to initialize first (root) item, if necessary
        /// </summary>
        /// <param name="obj"></param>
        void InitializeRoot(Node obj)
        {
            // First node that is created is the root node
            if (rootNode == null)
            {
                rootNode = (Node)obj;
                // FIX: outside events latch on to initialized *after* this InitializeRoot is called, meaning other folks will be notified
                // of assembly init AFTER global Complete event fires via rootNode.  
                // Not a killer, but incorrect and needs fixing
                rootNode.Initialized += item =>
                {
                    //Logger.Write("InitializeRoot completed");

                    if (Completed != null)
                        Completed();
                };
                rootNode.DigEnded += node =>
                {
                    DigChildren(node, assemblyList.Result);

                    // this is to indicate that the rootNode specifically has been dug, and to wake up any waiting
                    // initializers - since we have to wait for all digs to finish before we can actually begin
                    // initialization (because digs are what kick off IAsyncLoaders)
                    rootNodeEvh.Set();
                };
            }
        }

        Node rootNode;

        /// <summary>
        /// Set when dug has completed
        /// </summary>
        EventWaitHandle rootNodeEvh = new EventWaitHandle(false, EventResetMode.ManualReset);

        protected override DependencyBuilder<Assembly>.Node CreateNode(Assembly key)
        {
            return new Node(key, this);
        }

        /// <summary>
        /// Represents set of assembly nodes which are initializing asynchronously
        /// </summary>
		HashSet<Node> AsyncInitializing = new HashSet<Node>();

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
                loggerInit.LogInformation("Shutdown for assembly: " +
                    item.loader.GetType().GetTypeInfo().Assembly.FullName);
                ((ILoaderShutdown)item.loader).Shutdown();
            }
        }

        /// <summary>
        /// Fired when total initialization has completed
        /// </summary>
        public event Action Completed;

        /// <summary>
        /// Fired after all ILoaderAsync have completed.  May be a small delay since it is fired
        /// by initialization process and not ILoaderAsync servicer itself
        /// </summary>
        public event Action AsyncCompleted;

        public new class Node : InitializingDependencyBuilder<Assembly>.Node
        {
            AssemblyDependencyBuilder_NEW parent;
            internal readonly object loader;

            /// <summary>
            /// This item/assembly only should be dug into if it is either a rootnode OR it has loader code
            /// </summary>
            public override bool ShouldDig
            {
                get
                {
                    if (parent.rootNode == this)
                        return true;

                    if (loader is ILoader || loader is ILoaderAsync)
                        return true;

                    return false;
                }
            }

            protected override bool ShouldInitialize
            {
                get
                {
                    var hasLoader = loader is ILoader;

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


            /// <summary>
            /// Wait for all parent's IAsyncLoaders to fully complete
            /// </summary>
            void WaitForAsyncInitializers()
            {
                // this code results in ThreadPool being a queue of sorts as the task
                // waits.  Not a fantastic choice, but not so bad either
                // we do while comparison within lock statement  
                for (;;)
                {
                    Task task;

                    lock (parent.AsyncInitializing)
                    {
                        // equivelant of a while loop, but thread safe
                        if (parent.AsyncInitializing.Count == 0)
                            break;

                        // look for something a little faster than "First()" call
                        var _item = parent.AsyncInitializing.First();
                        task = _item.loaderAsyncTask;
                    }
                    task.Wait();
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
                WaitForAsyncInitializers();

                // Only fire overall AsyncCompleted event once, the first time we come in to the initialization phase
                // NOTE: This odd code location is because we need tree to be fully dug before being 100% sure that
                // an empty async initializer list == fully completed async initializers.
                if (!parent.IsAsyncInitialized)
                {
                    parent.IsAsyncInitialized = true;

                    if (parent.AsyncCompleted != null)
                        parent.AsyncCompleted();
                }
            }

            protected override void Initialize()
            {
#if UNUSED
				// We have to wait until root Node has been "dug" completely, otherwise
				// not all async loaders may have had a chance to start
				parent.rootNodeEvh.WaitOne();

				// Wait for "phase 1" async initializers to complete.  Waiting at this point makes sense, because a 
				// full "dig" as we waited for above means all async initializers have kicked off
				WaitForAsyncInitializers();

				// Only fire overall AsyncCompleted event once, the first time we come in to the initialization phase
				if (!parent.IsAsyncInitialized)
				{
					parent.IsAsyncInitialized = true;

					if (parent.AsyncCompleted != null)
						parent.AsyncCompleted();
				}
#endif
                if (InitBegin != null)
                    InitBegin(this);

                loggerInit.LogInformation("Initializing: " + Name + ": " + Children.Cast<Node>().
                    Select(x => x.Name + (x.IsInitialized ? " " : " not ") + "initialized").
                    ToString(", "));

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

                loggerInit.LogInformation("Initialized: " + Name);
            }

            public Node(Assembly key, AssemblyDependencyBuilder_NEW parent) : base(key)
            {
                this.parent = parent;
                loader = key.CreateInstance("Fact._Global.Loader");
                parent.InitializeRoot(this);
                /*			
	                // If this isn't an init/shutdown participating assembly, add it to an exclude cache so that next
				// time we startup we don't bother with it
				if (!(loader is ILoader || loader is ILoaderAsync || loader is ILoaderShutdown))
				{

				}*/
                if (loader is ILoaderShutdown)
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

            /// <summary>
            /// Have this so that we have a chance to latch on some event listeners
            /// </summary>
            public override void Start()
            {
                var loaderAsync = loader as ILoaderAsync;

                if (loaderAsync != null)
                {
                    loggerInit.LogInformation("Initializing (async kickoff): " + this.value.GetName().Name);

                    lock (parent.AsyncInitializing)
                    {
                        parent.AsyncInitializing.Add(this);
                    }

                    if (AsyncBegin != null)
                        AsyncBegin(this);

                    loaderAsyncTask = taskFactory.StartNew(() =>
                    {
                        loggerInit.LogInformation("Initializing (async actual): " + this.value.GetName().Name);

                        loaderAsync.Load();

                        loggerInit.LogInformation("Initialized (async): " + this.value.GetName().Name);
                    });

                    loaderAsyncTask.ContinueWith(t => exceptionPolicy.HandleException(t.Exception),
                        TaskContinuationOptions.OnlyOnFaulted);

                    loaderAsyncTask.ContinueWith(antecendent =>
                    {
                        lock (parent.AsyncInitializing)
                        {
                            parent.AsyncInitializing.Remove(this);
                        }

                        if (AsyncEnd != null)
                            AsyncEnd(this);
                    }, TaskContinuationOptions.NotOnFaulted);
                }
            }

            /// <summary>
            /// Called when ILoader initialize portion begins
            /// </summary>
            /// <remarks>
            /// These differenciate from "Initialized" event because this InitBegin specifically fires *AFTER* AsyncEnd, and
            /// also this InitEnd fires *BEFORE* Completed
            /// </remarks>
            public event Action<Node> InitBegin;

            /// <summary>
            /// Called when ILoaderAsync portion begins
            /// </summary>
            public event Action<Node> AsyncBegin;
            /// <summary>
            /// Called when ILoaderAsync portion is complete
            /// </summary>
            public event Action<Node> AsyncEnd;

            Task loaderAsyncTask;

            public override string Name { get { return value.GetName().Name; } }

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

        protected override object GetKey(Assembly key)
        {
            return key.FullName;
        }
    }
}

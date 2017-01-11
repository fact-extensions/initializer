using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Fact.Extensions.Initialization
{
    /// <summary>
    /// Like InitializingDependencyBuilder, but has a front-edged async initialization phase
    /// as well
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public abstract class InitializingAsyncDependencyBuilder<T> : InitializingDependencyBuilder<T>
    {
        static readonly ILogger logger = LogManager.CreateLogger<InitializingAsyncDependencyBuilder<T>>();
        static readonly ILogger loggerInit = LogManager.GetLogger("Initialization");
#if FEATURE_POLICY
        static readonly IPolicyProvider policyProvider = PolicyProvider.Get();
        static readonly ExceptionPolicy exceptionPolicy = policyProvider.GetExceptionPolicy();
#else
        static readonly ExceptionPolicy exceptionPolicy = new ExceptionPolicy();
#endif

        public InitializingAsyncDependencyBuilder()
        {
            NodeCreated += InitializingAsyncDependencyBuilder_NodeCreated;
        }

        private void InitializingAsyncDependencyBuilder_NodeCreated(DependencyBuilder<T>.Node _node)
        {
            // FIX: this is a slight fragility, it's conceivable a derived DependencyBuilder could
            // create a node which won't cast to this one.  
            var node = (INode)_node;

            node.AsyncBegin += __node => AddAsyncNode(node);
            node.AsyncEnd += __node => RemoveAsyncNode(node);
        }

        public bool IsAsyncInitialized { get; private set; }

        /// <summary>
        /// Represents set of nodes which are initializing asynchronously
        /// </summary>
		HashSet<INode> AsyncInitializing = new HashSet<INode>();

        public event Action AsyncCompleted;

        void AddAsyncNode(INode node)
        {
            lock(AsyncInitializing)
            {
                AsyncInitializing.Add(node);
            }
        }


        void RemoveAsyncNode(INode node)
        {
            lock(AsyncInitializing)
            {
                AsyncInitializing.Remove(node);
            }
        }


        /// <summary>
        /// Wait for all parent's async initializers to fully complete, and when so, 
        /// set IsAsyncInitialized = true
        /// </summary>
        /// <remarks>
        /// NOTE: This phase can very much be optimized so that an AsyncEnd strategically triggers
        /// IsAsyncInitialized = true.  Just need to work out filtering false-positives, which amounts
        /// to checking that EVERY candidate async loader has had the chance to start before allowing
        /// IsAsyncInitialized to be set true.
        /// NOTE ALSO: We may bypass above optimization of we move to the "begin synchro init while async
        /// init is still running" technique, in which case IsAsyncInitialized flag set is just a formality,
        /// and instead each Node will expect the conditions to be true to aggressively start a synchro init:
        /// a) the nodes it depends on are not presently in the AsyncInitializing set
        /// b) the nodes it depends on have had the chance to queue into AsyncInitializing set
        /// </remarks>
        protected void WaitForAsyncInitializers()
        {
            // this code results in ThreadPool being a queue of sorts as the task
            // waits.  Not a fantastic choice, but not so bad either
            // we do while comparison within lock statement  
            for (;;)
            {
                Task task;

                lock (AsyncInitializing)
                {
                    // equivelant of a while loop, but thread safe
                    if (AsyncInitializing.Count == 0)
                        break;

                    // look for something a little faster than "First()" call
                    var node = AsyncInitializing.First();
                    task = node.AsyncTask;
                }
                task.Wait();
            }

            // Only fire overall AsyncCompleted event once, the first time we come in to the initialization phase
            // NOTE: This odd code location is because we need tree to be fully dug before being 100% sure that
            // an empty async initializer list == fully completed async initializers.
            if (!IsAsyncInitialized)
            {
                IsAsyncInitialized = true;

                AsyncCompleted?.Invoke();
            }
        }


        public interface INode
        {
            /// <summary>
            /// Called when async init portion begins
            /// </summary>
            event Action<INode> AsyncBegin;
            /// <summary>
            /// Called when async init portion is complete
            /// </summary>
            event Action<INode> AsyncEnd;

            Task AsyncTask { get; }
        }

        public abstract new class Node : InitializingDependencyBuilder<T>.Node, INode
        {
            public Node(T value) : base(value) { }

            /// <summary>
            /// Called when async init portion begins
            /// </summary>
            public event Action<INode> AsyncBegin;
            /// <summary>
            /// Called when async init portion is complete
            /// </summary>
            public event Action<INode> AsyncEnd;


            protected abstract void DoAsyncLoad();


            /// <summary>
            /// Async and non-Async nodes can be interspersed, so indicate which type this is
            /// Remember in either case a Sync init still happens
            /// </summary>
            public virtual bool IsAsync { get; }

            Task asyncTask;

            public Task AsyncTask => asyncTask;

            /// <summary>
            /// Have this so that we have a chance to latch on some event listeners
            /// </summary>
            public override void Start()
            {
                if (IsAsync)
                {
                    loggerInit.LogInformation("Initializing (async kickoff): " + Name);

                    AsyncBegin?.Invoke(this);

                    asyncTask = taskFactory.StartNew(() =>
                    {
                        loggerInit.LogInformation("Initializing (async actual): " + Name);

                        DoAsyncLoad();

                        loggerInit.LogInformation("Initialized (async): " + Name);
                    });

                    asyncTask.ContinueWith(t => exceptionPolicy.HandleException(t.Exception),
                        TaskContinuationOptions.OnlyOnFaulted);

                    asyncTask.ContinueWith(antecendent =>
                    {
                        AsyncEnd?.Invoke(this);
                    }, TaskContinuationOptions.NotOnFaulted);
                }
            }
        }
    }
}

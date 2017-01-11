using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using Fact.Extensions.Collection;

namespace Fact.Extensions.Initialization
{
    /// <summary>
    /// Extension of DependencyBuilder which facilities initialization of Items (nodes) found.
    /// Initialization is managed in such a way where multiple nodes may initialize at once, so long
    /// as no dependency collision occurs
    /// </summary>
    /// <remarks>
    /// Formal initialization phase does not begin until full Dig has completed
    /// Derived classes may augment this with an informal init phase of their own, such as an async
    /// init phase kicked off per Node discovered
    /// </remarks>
    /// <typeparam name="T"></typeparam>
    public abstract class InitializingDependencyBuilder<T> : DependencyBuilder<T>
    {
        static readonly ILogger loggerInit = LogManager.GetLogger("Initialization");
        static readonly ILogger logger = LogManager.CreateLogger<InitializingDependencyBuilder<T>>();

#if CUSTOM_THREADPOOL
        // static fields are initialized in order of appearance.  See
        // http://msdn.microsoft.com/en-us/library/aa645757(VS.71).aspx
        internal static readonly LimitedConcurrencyLevelTaskScheduler taskScheduler = 
            new LimitedConcurrencyLevelTaskScheduler(CpuCount);
        /// <summary>
        /// Don't want TOO many threads, since Initialization itself may be running
        /// concurrently with some other stuff (somtimes we want to bring a UI up)
        /// and itself has its own thread which kicks off threads on this taskFactory
        /// </summary>
        internal static readonly TaskFactory taskFactory = new TaskFactory(taskScheduler);


        static InitializingDependencyBuilder()
        {
            taskScheduler.ExceptionHandler += (task, e) => 
            {
                logger.Fatal("InitializingDependencyBuilder failure: task ID " + task.Id + ", message = " +
                    e.InnerExceptions.Select(x => x.Message).ToString(","), e);

                if (e.InnerException != null)
                    logger.FatalWithInspection("InitializingDependencyBuilder failure: ", e);

                // FIX: As per the bug reported in http://connect.microsoft.com/VisualStudio/feedbackdetail/view/931506/exceptions-occuring-on-threadpool-in-addition-possibly-with-self-hosted-debug-wcf-bring-down-entire-vs2013-environment
                // this will bring down the entire development environment
                throw e;
            };
        }
#else
        // .NET 3.5 compatibility version doesn't have an advanced task factory, just
        // a simplistic shim
        internal static readonly TaskFactory taskFactory = Task.Factory;
#endif

        static int CpuCount
        {
            get
            {
                var logicalCPUs = Environment.ProcessorCount;
                var countMapper = new int[]
                {
                    2, // 0 cpu's, would be error 
                    3, // 1 cpu's, 3 thread minimum
                    3, // 2 cpu's, 3 thread minimum
                    4  // 3 cpu's, 4 thread minimum
                    // anything higher than 3, just match thread count directly to number of available 
                    // CPUs
                };

                return logicalCPUs < 4 ? countMapper[logicalCPUs] : logicalCPUs;
            }
        }

        //internal static readonly TaskFactory taskFactory = Task.Factory;

        new abstract public class Node : DependencyBuilderNode<T>
        {

            public bool IsInitialized
            {
                get { return initialized; }
                private set { initialized = value; }
            }

            public abstract string Name { get; }

            public Node(T value) : base(value)
            {
                ChildAdded += child =>
                {
                    var _child = (Node)child;

                    // if child is uninitialized, then be sure to add this to the uninitialized
                    // list for this item
                    if (!_child.initialized)
                        ChildrenUninitialized.Add(_child);

                    // once said child is initialized, then be sure to remove it from
                    // the uninitialized list for this item
                    _child.Initialized += __child =>
                    {
                        ChildrenUninitialized.Remove(_child);

                        // after removal, if this item itself is not initialized
                        // but ready to be, then attempt to do so
                        EvalInitialize(false);
                    };
                };

                DigEnded += Node_DigEnded;
            }

            bool dug;

            /// <summary>
            /// Set to true when this item has gone through a complete dig phase.
            /// It is only after a complete dig phase when it is safe to attempt initialize
            /// this item
            /// </summary>
            public bool IsDug { get { return dug; } }

            /// <summary>
            /// Evaluate whether conditions are right to initialize this item, and if so,
            /// kick off a thread to do so
            /// </summary>
            /// <param name="direct">true = invoked from dig-end event, false = invoked from child initialized event</param>
            void EvalInitialize(bool direct)
            {
                // only enter here when fully "dug" otherwise ChildrenUninitialized may not be 
                // fully filled out
                if (!IsInitialized && !initializing && IsDug)
                {
                    // If dig has ended and all children items are initialized, then immediately
                    // initialize this item also
                    if (ChildrenUninitialized.Count == 0)
                    {
                        if (direct)
                            loggerInit.LogInformation("Initializing: " + Name + " (DIRECT)");

                        // Not every type T desires initialization, if this type T needs none,
                        // don't consume time and resources spawning a new thread
                        // FIX: MONO Gtk Test app uncovers a bug here where ShouldInitialize
                        // itself results in an Completed event being called, returns FALSE
                        // and then this if block performs an Initialization event, which
                        // in turn performs another Completed event
                        if (ShouldInitialize)
                        {
                            initializing = true;
                            initializingTask = taskFactory.StartNew(() =>
                            {
                                WaitForDependencies();

                                Initializing?.Invoke(this);

                                loggerInit.LogInformation("Initializing: " + Name + ": " + Children.Cast<Node>().
                                    Select(x => x.Name + (x.IsInitialized ? " " : " not ") + "initialized").
                                    ToString(", "));

                                Initialize();

                                initializing = false;
                                IsInitialized = true;

                                Initialized?.Invoke(this);
                            });
                        }
                        else
                        {
                            WaitForDependencies();

                            IsInitialized = true;
                            Initialized?.Invoke(this);
                        }
                    }
                }
            }

            /// <summary>
            /// Returns true if type T desires its own initialization phase.  Return false
            /// if no thread for initialization should be created (still will fire off Initialized
            /// event and set IsInitialized = true though)
            /// </summary>
            protected virtual bool ShouldInitialize
            {
                get { return true; }
            }

            /// <summary>
            /// Before Initialize() is called, there may be dependencies this
            /// Node is waiting on.  This method performs this task, and fires
            /// off any relevant "dependencies completed" events as well
            /// 
            /// Remember not all items receive an Initialize() call, but these
            /// items may still have dependencies - so even if ShouldInitialize
            /// returns FALSE, WaitForDependencies is still called
            /// </summary>
            protected virtual void WaitForDependencies() { }

            /// <summary>
            /// Perform initialization of this node.
            /// Called when all children are fully initialized
            /// </summary>
            protected abstract void Initialize();

            Task initializingTask;

            /// <summary>
            /// Nodes are evaluated all the way to the bottom, and when we cannot
            /// dig any further down into nodes - either because they have no children, or
            /// because all children are initialized, then initialize the node.
            /// </summary>
            /// <param name="obj"></param>
            void Node_DigEnded(DependencyBuilder<T>.Node obj)
            {
                dug = true;

                // If dig has ended and all children items are initialized, then immediately
                // initialize this item also
                EvalInitialize(true);
            }

            bool initialized;
            public bool initializing;

            /// <summary>
            /// List of children still requiring initialization
            /// </summary>
            public HashSet<Node> ChildrenUninitialized = new HashSet<Node>();

            /// <summary>
            /// FIred when this particular node begins its initialization phase
            /// </summary>
            public event Action<Node> Initializing;

            /// <summary>
            /// Fired when this particular node and its T finishes initializing
            /// </summary>
            public event Action<Node> Initialized;
        }
    }
}

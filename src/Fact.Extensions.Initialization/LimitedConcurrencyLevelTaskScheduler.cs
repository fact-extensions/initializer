using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Fact.Extensions.Initialization
{
#if CUSTOM_THREADPOOL
    // lifted from MSDN: http://msdn.microsoft.com/en-us/library/ee789351.aspx
    // Provides a task scheduler that ensures a maximum concurrency level while  
    // running on top of the thread pool. 
    public class LimitedConcurrencyLevelTaskScheduler : TaskScheduler
    {
        static readonly ILogger logger = LogManager.GetCurrentClassLogger();

        // MB: logger & tuning, also added by me, initialization here
        static LimitedConcurrencyLevelTaskScheduler()
        {
            /* Shelving this until we break out Tuning to individually overridables tunings
            var tuning = new Tuning();
            var type = typeof(LimitedConcurrencyLevelTaskScheduler);

            Global.Container.Register(Component.For<Tuning>().Instance(tuning).Named(type.Name));*/
        }

#if UNUSED
        static readonly Tuning tuning = Tuning.GetTuning();
#endif

        // Indicates whether the current thread is processing work items.
        [ThreadStatic]
        private static bool _currentThreadIsProcessingItems;

        // The list of tasks to be executed  
        private readonly LinkedList<Task> _tasks = new LinkedList<Task>(); // protected by lock(_tasks) 

        // The maximum concurrency level allowed by this scheduler.  
        private readonly int _maxDegreeOfParallelism;

        // Indicates whether the scheduler is currently processing work items.  
        private int _delegatesQueuedOrRunning = 0;

        /// <summary>
        /// Fired when a task fails.  Normally this task scheduler, at least for MONO,
        /// just silently eats up the exception
        /// </summary>
        /// <param name="maxDegreeOfParallelism">Max degree of parallelism.</param>
        public event Action<Task, AggregateException> ExceptionHandler;

        // Creates a new instance with the specified degree of parallelism.  
        public LimitedConcurrencyLevelTaskScheduler(int maxDegreeOfParallelism)
        {
            if (maxDegreeOfParallelism < 1) throw new ArgumentOutOfRangeException("maxDegreeOfParallelism");
            _maxDegreeOfParallelism = maxDegreeOfParallelism;
        }

        // Queues a task to the scheduler.  
        protected sealed override void QueueTask(Task task)
        {
#if DEBUG
            var timeout = DateTime.Now; // MB: adding timeout debugging to see if slowdowns are coming from here
#endif
            // Add the task to the list of tasks to be processed.  If there aren't enough  
            // delegates currently queued or running to process tasks, schedule another.  
            lock (_tasks)
            {
#if DEBUG
                /*
                tuning.Assert.True("timeout",
                    DateTime.Now.Subtract(timeout).TotalMilliseconds > 250);*/

                if (DateTime.Now.Subtract(timeout).TotalMilliseconds > 250)
                    logger.Warn("TryDequeue: potential slowdown");
#endif

                _tasks.AddLast(task);
                if (_delegatesQueuedOrRunning < _maxDegreeOfParallelism)
                {
                    ++_delegatesQueuedOrRunning;
                    NotifyThreadPoolOfPendingWork();
                }
            }
        }

        // Inform the ThreadPool that there's work to be executed for this scheduler.  
        private void NotifyThreadPoolOfPendingWork()
        {
            Task.Factory.StartNew(() =>
            {
                // Note that the current thread is now processing work items. 
                // This is necessary to enable inlining of tasks into this thread.
                _currentThreadIsProcessingItems = true;
                try
                {
                    // Process all available items in the queue. 
                    while (true)
                    {
                        Task item;
                        lock (_tasks)
                        {
                            // When there are no more items to be processed, 
                            // note that we're done processing, and get out. 
                            if (_tasks.Count == 0)
                            {
                                --_delegatesQueuedOrRunning;
                                break;
                            }

                            // Get the next item from the queue
                            item = _tasks.First.Value;
                            _tasks.RemoveFirst();
                        }

                        // Execute the task we pulled out of the queue 
                        base.TryExecuteTask(item);

                        if(item.IsFaulted)
                        {
                            if(ExceptionHandler != null)
                                ExceptionHandler(item, item.Exception);
                            logger.Error("task id = " + item.Id);
                            foreach(var e in item.Exception.InnerExceptions)
                            {
                                logger.Error("LimitedConcurrencyLevelTaskScheduler error = " + e.Message, e);
                                logger.Error("LimitedConcurrencyLevelTaskScheduler error = " + e.StackTrace, e);
                            }
                        }
                    }
                }
                // We're done processing items on the current thread 
                finally { _currentThreadIsProcessingItems = false; }
            });
        }

        // Attempts to execute the specified task on the current thread.  
        protected sealed override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
        {
            // If this thread isn't already processing a task, we don't support inlining 
            if (!_currentThreadIsProcessingItems) return false;

            // If the task was previously queued, remove it from the queue 
            if (taskWasPreviouslyQueued)
                // Try to run the task.  
                if (TryDequeue(task))
                    return base.TryExecuteTask(task);
                else
                    return false;
            else
                return base.TryExecuteTask(task);
        }

        // Attempt to remove a previously scheduled task from the scheduler.  
        protected sealed override bool TryDequeue(Task task)
        {
#if DEBUG
            var timeout = DateTime.Now; // MB: adding timeout debugging to see if slowdowns are coming from here
#endif
            lock (_tasks)
            {
#if DEBUG
                if (DateTime.Now.Subtract(timeout).TotalMilliseconds > 250)
                    logger.Warn("TryDequeue: potential slowdown");
#endif
                return _tasks.Remove(task);
            }
        }

        // Gets the maximum concurrency level supported by this scheduler.  
        public sealed override int MaximumConcurrencyLevel { get { return _maxDegreeOfParallelism; } }

        // Gets an enumerable of the tasks currently scheduled on this scheduler.  
        protected sealed override IEnumerable<Task> GetScheduledTasks()
        {
            bool lockTaken = false;
            try
            {
                Monitor.TryEnter(_tasks, ref lockTaken);
                if (lockTaken) return _tasks;
                else
                {
                    // MB: Adding log entry to see if perhaps this is generating a slowdown
                    logger.Error("GetScheduledTasks: couldn't acquire lock");
                    throw new NotSupportedException();
                }
            }
            finally
            {
                if (lockTaken) Monitor.Exit(_tasks);
            }
        }
    }
#endif
}

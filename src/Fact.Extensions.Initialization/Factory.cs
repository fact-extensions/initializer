#if NET40
#define CUSTOM_THREADPOOL
#endif

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Fact.Extensions;

#if CUSTOM_THREADPOOL
//using Castle.MicroKernel.Registration;
#endif

using System.Threading;
using System.Threading.Tasks;
using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Fact.Extensions.Initialization
{
    /// <summary>
    /// TODO: Consolodate this with the Collections version
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public interface IFactory<T>
    {
        T Create();
    }
}

namespace Fact.Extensions.Initialization
{
    /// <summary>
    /// Collections tend to have an overhead to their internal lookup
    /// The "CanHandle" call is a specialized version which yields metadata which in turn can
    /// be used to optimize the next lookup call.
    /// For property bag stuff, it's like a "Contains" call
    /// </summary>
    /// <typeparam name="TKey"></typeparam>
    /// <remarks>
    /// Note these are not directly related to IAggregator UI validation interfaces
    /// </remarks>
    public interface IAggregateWithMeta<TKey>
    {
        bool CanHandle(TKey key, out object meta);
    }

    /// <summary>
    /// TODO: This could use a better name.  IAggregate was chosen because aggregates sometimes are iterated over
    /// and inspected item by item to see which item can be handled
    /// </summary>
    /// <typeparam name="TKey"></typeparam>
    public interface IAggregate<TKey>
    {
        /// <summary>
        /// Returns whether this aggregate key can be processed
        /// </summary>
        /// <param name="key"></param>
        /// <returns></returns>
        /// <remarks>
        /// It is up to each aggregate to determine the meaning of processable/handleable
        /// </remarks>
        bool CanHandle(TKey key);
    }


    public static class IAggregate_Extensions
    {
        /// <summary>
        /// Iterate through can-handle items then execute specialized handler
        /// when one is found
        /// </summary>
        /// <typeparam name="TKey"></typeparam>
        /// <typeparam name="TAggregateItem"></typeparam>
        /// <param name="aggregates"></param>
        /// <param name="key"></param>
        /// <param name="handler"></param>
        /// <param name="multi">when false, handles first item where "CanHandle = true".  when true, handles all items which are "CanHandle = true"</param>
        public static bool Execute<TKey, TAggregateItem>(this IEnumerable<TAggregateItem> aggregates, TKey key, Action<TAggregateItem, TKey> handler, bool multi = false)
            where TAggregateItem: IAggregate<TKey>
        {
            bool found = false;

            foreach(var ai in aggregates)
            {
                if(ai.CanHandle(key))
                {
                    handler(ai, key);
                    if (!multi) return true;
                    found = true;
                }
            }

            return found;
        }
    }

    public interface IFactory<TInput, TOutput>
    {
        bool CanCreate(TInput id);

        TOutput Create(TInput id);
    }


    /// <summary>
    /// Meta provides an optimization cache area, since often the CanCreate does a lookup operation
    /// of some kind which then the create may have to repeat
    /// </summary>
    /// <typeparam name="TInput"></typeparam>
    /// <typeparam name="TOutput"></typeparam>
    public interface IFactoryWithMeta<TInput, TOutput> : IFactory<TInput, TOutput>
    {
        bool CanCreate(TInput id, out object meta);

        TOutput Create(TInput id, object meta);
    }


    public class FactoryAggregator<TInput, TOutput> : IFactoryWithMeta<TInput, TOutput>
    {
        LinkedList<IFactory<TInput, TOutput>> candidates = new LinkedList<IFactory<TInput, TOutput>>();

        public IFactory<TInput, TOutput> GetCandidate(TInput id)
        {
            return candidates.FirstOrDefault(c => c.CanCreate(id));
            /*
            foreach (var c in candidates)
            {
                if (c.CanCreate(id))
                    return c;
            }

            return null;*/
        }

        internal class Meta
        {
            internal IFactory<TInput, TOutput> candidate;
            internal object meta; // meta associated with the found candidate linked to the ID
#if DEBUG
            internal TInput id;
#endif
        }

        IFactory<TInput, TOutput> GetCandidate(TInput id, out object meta)
        {
            foreach (var c in candidates)
            {
                var c_meta = c as IFactoryWithMeta<TInput, TOutput>;

                if (c_meta != null)
                {
                    if (c_meta.CanCreate(id, out meta))
                        return c;
                }
                else
                {
                    if (c.CanCreate(id))
                    {
                        meta = null;
                        return c;
                    }
                }
            }

            meta = null;
            return null;
        }

        public bool CanCreate(TInput id)
        {
            return GetCandidate(id) != null;
        }


        public bool CanCreate(TInput id, out object meta)
        {
            var c = GetCandidate(id, out meta);
            if(c != null)
            {
                var _meta = new Meta() { meta = meta, candidate = c };
#if DEBUG
                _meta.id = id;
#endif
                meta = _meta;
                return true;
            }
            return false;
        }

        public TOutput Create(TInput id)
        {
            var c = GetCandidate(id);

            if (c != null) return c.Create(id);

            throw new KeyNotFoundException();
        }


        public TOutput Create(TInput id, object meta)
        {
            var _meta = meta as Meta;

            if(_meta != null)
            {
                var c_meta = _meta.candidate as IFactoryWithMeta<TInput, TOutput>;

                if(c_meta != null)
                {
#if DEBUG
                    if (!Object.Equals(_meta.id, id))
                        throw new ArgumentOutOfRangeException();
#endif
                    return c_meta.Create(id, _meta.meta);
                }
                else
                {
                    return _meta.candidate.Create(id);
                }
            }

            var c = GetCandidate(id);

            if (c != null) return c.Create(id);

            throw new KeyNotFoundException();
        }

        public void Add(IFactory<TInput, TOutput> candidate)
        {
            candidates.AddLast(candidate);
        }
    }


    public class DelegateFactory<TInput, TOutput> : IFactory<TInput, TOutput>
    {
        readonly Func<TInput, bool> canCreate;
        readonly Func<TInput, TOutput> create;

        public DelegateFactory(Func<TInput, bool> canCreate, Func<TInput, TOutput> create)
        {
            this.canCreate = canCreate;
            this.create = create;
        }

        public bool CanCreate(TInput id)
        {
            return canCreate(id);
        }

        public TOutput Create(TInput id)
        {
            return create(id);
        }
    }

#if UNUSED
    public class FactoryAggregator<TInput, TOutput> : IFactory<TInput, TOutput>
    {
        Lookup<TInput, IFactory<TInput, TOutput>> canService;



        public bool CanCreate(TInput id)
        {
            var result = canService[id];

            if (result != null && result.CountTo(1) > 0)
                return true;

            return true;
        }

        public TOutput Create(TInput id)
        {
            return canService[id].First().Create(id);
        }
    }
#endif

    public class DependencyBuilder
    {
    }

    public abstract class DependencyBuilder<T> : DependencyBuilder
    {
        static readonly ILogger logger = LogManager.CreateLogger<DependencyBuilder<T>>();

        /// <summary>
        /// This class represents a node in the dependency tree
        /// </summary>
        public abstract class Item
        {
            public T value;

            LinkedList<Item> children = new LinkedList<Item>();
            LinkedList<Item> parents = new LinkedList<Item>();

            public IEnumerable<Item> Children { get { return children; } }
            public IEnumerable<Item> Parents { get { return parents; }}

            /// <summary>
            /// Occurs when child is initially added, but before it is itself dug into
            /// </summary>
            public event Action<Item> ChildAdded;
            public event Action<Item> ParentAdded;

            /// <summary>
            /// When the dig phase for this item ends, fire this
            /// End of dig phase should have children and parents
            /// fully built out
            /// </summary>
            public event Action<Item> DigEnded;

            public void AddChild(Item child)
            {
                children.AddLast(child);

                if (ChildAdded != null)
                    ChildAdded(child);
            }


            public void AddParent(Item parent)
            {
                parents.AddLast(parent);

                if (ParentAdded != null)
                    ParentAdded(parent);
            }

            internal void DoDigEnded()
            {
                if (DigEnded != null)
                    DigEnded(this);
            }

            public abstract IEnumerable<T> GetChildren();

            /// <summary>
            /// Have this so that we have a chance to latch on some event listeners to Item
            /// before we get all crazy with threads
            /// </summary>
            public virtual void Start() { }

            public virtual bool ShouldDig { get { return true; } }
        }

        Dictionary<object, Item> lookup = new Dictionary<object, Item>();
        HashSet<T> alreadyInspected = new HashSet<T>();

        /// <summary>
        /// Fired right when an item gets created, before any Dig processing occurs
        /// </summary>
        public event Action<Item> ItemCreated;

        /// <summary>
        /// Key mangler.  Sometimes the default key type T is not suitable for doing lookups
        /// </summary>
        /// <param name="key"></param>
        /// <returns></returns>
        /// <remarks>
        /// Because we can have two different assemblies loaded with the same name.  Here's one possible (but
        /// not ideal) place to resolve it
        /// </remarks>
        protected virtual object GetKey(T key)
        {
            return key;
        }

        /// <summary>
        /// Acquires the already-existing Item for the dependency graph,
        /// otherwise creates it and tracks it for next time
        /// </summary>
        /// <param name="key"></param>
        /// <returns></returns>
        protected Item GetValue(T key)
        {
            Item item;
            object _key = GetKey(key);

            if(!lookup.TryGetValue(_key, out item))
            {
                item = CreateItem(key);
                item.value = key;

                if (ItemCreated != null)
                    ItemCreated(item);

                item.Start();

                lookup.Add(_key, item);
            }

            return item;
        }


        /// <summary>
        /// Factory method for Items (nodes)
        /// </summary>
        /// <param name="key"></param>
        /// <returns></returns>
        protected abstract Item CreateItem(T key);

        /// <summary>
        /// Low level dig method, for internal/reuse
        /// </summary>
        /// <param name="value"></param>
        /// <param name="item"></param>
        protected void Dig(Item item, T value, IEnumerable<T> children)
        {
            foreach (var child in children)
            {
                var childItem = GetValue(child);

                if (childItem.ShouldDig)
                {
                    item.AddChild(childItem);
                    Dig(child, value);
                }
            }
        }

        /// <summary>
        /// Dig all the way to the bottom, then build from the bottom up
        /// </summary>
        /// <param name="value"></param>
        public Item Dig(T value, T parent)
        {
            // FIX: kludgey
            alreadyInspected.Add(parent);

            var item = GetValue(value);

            if (parent != null)
                item.AddParent(GetValue(parent));

            if (alreadyInspected.Contains(value))
                return item;

            try
            {
                Dig(item, value, item.GetChildren());
                /*
                foreach (var child in item.GetChildren())
                {
                    var childItem = GetValue(child);

                    if (childItem.ShouldDig)
                    {
                        item.AddChild(childItem);
                        Dig(child, value);
                    }
                }*/
            }
            catch(Exception e)
            {
                logger.LogDebug("DependencyBuilder::Dig failure on inspecting children of: " + RetrieveDescription(value));
                logger.LogDebug("DependencyBuilder::Dig exception: " + e.Message);
                throw;
            }

            alreadyInspected.Add(value);
            item.DoDigEnded();
            return item;
        }


        protected virtual string RetrieveDescription(T value)
        {
            return value.ToString();
        }
    }

    public abstract class _DependencyBuilderItem<T> : DependencyBuilder<T>.Item
    {
        public abstract class Meta
        {
            public HashSet<_DependencyBuilderItem<T>> Dependencies = new HashSet<_DependencyBuilderItem<T>>();
            public bool IsSatisfied;
            public readonly _DependencyBuilderItem<T> Item;

            /// <summary>
            /// This is the core method to Satisfy any underlying dependencies
            /// </summary>
            protected abstract void Satisfy ();

            /// <summary>
            /// Return whether we should bother to attempt to satisfy underlying dependencies
            /// </summary>
            protected abstract bool ShouldSatisfy { get; }

            public bool IsAsync { get; set; }

            public event Action<Meta> BeginSatisfy;
            public event Action<Meta> Satisfied;

            protected Task satisfyTask;


            /// <summary>
            /// TODO: Document what this does
            /// </summary>
            public virtual void EvalSatisfy()
            {
                // If all dependencies are now gone
                if (Dependencies.Count == 0) 
                {
                    // Check to see if we have a specific handler for when dependencies
                    // are satisfied
                    if (ShouldSatisfy) 
                    {
                        // Fire off event notifying we're about to do specific
                        // handler for satisfy
                        if(BeginSatisfy != null)
                            BeginSatisfy (this);

                        if (IsAsync) 
                        {
                            satisfyTask = Task.Factory.StartNew (() =>
                            {
                                Satisfy();

                                if(Satisfied != null)
                                    Satisfied(this);
                            });
                        }
                        else
                            Satisfy ();
                    }

                    // This is fired on the async handler when in async mode
                    if (!IsAsync && Satisfied != null)
                        Satisfied (this);
                }
            }

            public readonly string Key;

            public Meta(string key, _DependencyBuilderItem<T> item) 
            {
                Key = key; 
                Item = item; 
            }


            /// <summary>
            /// TODO: Document what this method does
            /// </summary>
            /// <param name="_item"></param>
            public void Handler(DependencyBuilder<T>.Item _item)
            {
                var item = (_DependencyBuilderItem<T>)_item; 
                var itemMeta = item.Dependencies[Key];

                // If _item has not satisfied its own dependencies, then
                // we must depend on it since dependencies cascade
                if(!itemMeta.IsSatisfied)
                {
                    Dependencies.Add(item);

                    Satisfied += __item =>
                    {
                        Dependencies.Remove(item);
                        EvalSatisfy();
                    };
                }
            }

            public virtual void Start() {}
        }

        Dictionary<string, Meta> Dependencies = new Dictionary<string, Meta>();

        /// <summary>
        /// Add this to the dependency list.  Next-generation code, not fully functional yet
        /// </summary>
        /// <param name="meta"></param>
        protected void Add(Meta meta)
        {
            Dependencies.Add (meta.Key, meta);
        }
    }


    public abstract class ChildSyncMeta<T> : _DependencyBuilderItem<T>.Meta
    {
        public ChildSyncMeta(string key, _DependencyBuilderItem<T> item) :
            base(key, item)
        {
            // when child is added, register child as something this item depends on
            // to reach satisfied state
            item.ChildAdded += Handler;
            // when dig phase completes, check to see if we can satisfy the dependency
            // (in this case, check to see that all parents have uninitialized, so that
            // we can too)
            item.DigEnded += _item => EvalSatisfy ();
        }
    }


    public abstract class ChildAsyncMeta<T> : _DependencyBuilderItem<T>.Meta
    {
        public ChildAsyncMeta(string key, _DependencyBuilderItem<T> item) :
            base(key, item)
        {
            // when child is added, register child as something this item depends on
            // to reach satisfied state.  However, this is only tracked so that outside
            // processes know when the entire ChildAsyncMeta tree is done.  The tree
            // 
            item.ChildAdded += _item => EvalSatisfy2();

            item.DigEnded += HandleDigEnded;
        }

        void HandleDigEnded (DependencyBuilder<T>.Item obj)
        {
            
        }

        public void EvalSatisfy2 ()
        {
            satisfyTask = Task.Factory.StartNew (() => 
            {
                Satisfy();
                /*
                if(Satisfied != null)
                    Satisfied(this);*/

                // dependencies work in reverse for async, since we don't know all the proper dendencies
                // until the end of the dig but don't want to wait until the end of the dig
                Dependencies.Add(Item);

				var children = Item.Children as ICollection<DependencyBuilder<T>.Item>;
				var count = children == null ? Item.Children.Count() : children.Count;

                // If we reach the same number of dependencies accumulated as children AND we are
                // finished digging, then do stuff
                if(Dependencies.Count == count)
                {
                }
            });
        }

        public override void Start ()
        {
            // begin evaluations immediately
            EvalSatisfy ();
        }
    }

#if UNUSED
    public class AssemblyDependencyBuilder_TEST : DependencyBuilder<Assembly>
    {
        internal class InitAsyncMeta : ChildAsyncMeta<Assembly>
        {
            internal InitAsyncMeta(Item item) : base("initAsync", item) {}

            protected override void Satisfy ()
            {
                var item = (Item)Item;
                ((ILoaderAsync)item.loader).Load ();
            }

            protected override bool ShouldSatisfy 
            {
                get 
                {
                    return ((Item)Item).loader is ILoaderAsync;
                }
            }
        }

        internal class InitSyncMeta : ChildSyncMeta<Assembly>
        {
            internal InitSyncMeta(Item item) : base("initSync", item) {}

            protected override void Satisfy ()
            {
                ((ILoader)((Item)Item).loader).Initialize ();
            }

            protected override bool ShouldSatisfy 
            {
                get 
                {
                    return ((Item)Item).loader is ILoader;
                }
            }
        }

        public new class Item : _DependencyBuilderItem<Assembly>
        {
            public readonly object loader;

            public Item(Assembly key)
            {
                loader = key.CreateInstance("Fact._Global.Loader");
                var initAsync = new InitAsyncMeta(this);
                Add(initAsync);
                Add(new InitSyncMeta(this));
            }

            public override IEnumerable<Assembly> GetChildren ()
            {
                // FIX: need to not duplicate this from AssemblyDependencyBuilder._Item
                return value.GetReferencedAssemblies().
                    Where(x => Initializer.SkipAssembly.FirstOrDefault(y => x.Name.StartsWith(y)) == null).
                        Where(x => !Initializer.Inspected.Contains(x.Name)).
                        Select(x => System.Reflection.Assembly.Load(x));
            }
        }

        protected override DependencyBuilder<Assembly>.Item CreateItem (Assembly key)
        {
            return new Item (key);
        }
    }
#endif


#if !NETCORE
    public class AssemblyUninitializeMeta : _DependencyBuilderItem<Assembly>.Meta
    {
        public AssemblyUninitializeMeta(_DependencyBuilderItem<Assembly> item) : 
            base("uninitialize", item)
        {
            // when parent is added, register parent as something this item depends on
            // for uninitialization
            item.ParentAdded += Handler;
            // when dig phase completes, check to see if we can satisfy the dependency
            // (in this case, check to see that all parents have uninitialized, so that
            // we can too)
            item.DigEnded += _item => EvalSatisfy ();
        }

        protected override bool ShouldSatisfy 
        {
            get 
            {
                var item = ((AssemblyDependencyBuilder._Item)Item);
                return item.loader is ILoaderShutdown;
            }
        }

        protected override void Satisfy ()
        {
            var item = ((AssemblyDependencyBuilder._Item)Item);
            (( ILoaderShutdown)item.loader).Shutdown();
        }
    }
#endif

    public abstract class UninitializingItem<T> : _DependencyBuilderItem<T>
    {
        HashSet<UninitializingItem<T>> ParentUninitialized = new HashSet<UninitializingItem<T>>();

        public bool IsUninitialized { get; private set; }

        /// <summary>
        /// Uninitialize events are still fired, but the actual Uninitialize call is skipped
        /// if this is false.  Events still need to fire because consider parent A, child B,
        /// then child C of child B.  If child B has no uninitialization code but A and C do
        /// we still need to "skip a generation" so that C properly knows when to uninit.
        /// </summary>
        /// <value><c>true</c> if should uninitialize; otherwise, <c>false</c>.</value>
        protected abstract bool ShouldUninitialize { get; }

        public class UninitializeMeta : Meta
        {
            protected override void Satisfy ()
            {
                throw new NotImplementedException ();
                /*
                var item = Item;

                var loader = ((AssemblyDependencyBuilder._Item)Item).loader;

                ((ILoaderShutdown)loader).Shutdown ();*/
            }

            protected override bool ShouldSatisfy 
            {
                get 
                {
                    throw new NotImplementedException ();
                    /*
                    ((AssemblyDependencyBuilder._Item)Item).loader is ILoaderShutdown;

                    */
                }
            }

            public UninitializeMeta(string key, _DependencyBuilderItem<T> item) : base(key, item) 
            {
                //item.ParentAdded += Handler;
            }
        }

        public UninitializingItem()
        {
            // handler for uninitialization is created and registered
            var uninitializeMeta = new UninitializeMeta ("parent", this);
            Add (uninitializeMeta);

            // when parent is added, register parent as something this item depends on
            // for uninitialization
            ParentAdded += uninitializeMeta.Handler;

            // when dig phase completes, check to see if we can satisfy the dependency
            // (in this case, check to see that all parents have uninitialized, so that
            // we can too)
            DigEnded += item => 
            {
                uninitializeMeta.EvalSatisfy();
            };
        }


        public event Action<UninitializingItem<T>> Uninitialized;


        void EvalUninitialize()
        {
            if(ParentUninitialized.Count == 0)
            {
                if (ShouldUninitialize) 
                {
                    Uninitialize ();
                }

                if (Uninitialized != null)
                    Uninitialized (this);
            }
        }

        protected abstract void Uninitialize ();
    }


#if !NETCORE
    public class AssemblyUninitializingDependencyBuilder : DependencyBuilder<Assembly>
    {
        public class Item : UninitializingItem<Assembly>
        {
            object loader = null;

            protected override bool ShouldUninitialize 
            {
                get { return loader is ILoaderShutdown; }
            }

            protected override void Uninitialize ()
            {
                ((ILoaderShutdown)loader).Shutdown ();
            }

            public override IEnumerable<Assembly> GetChildren ()
            {
                // FIX: need to not duplicate this from AssemblyDependencyBuilder._Item
                return value.GetReferencedAssemblies().
                    Where(x => Initializer.SkipAssembly.FirstOrDefault(y => x.Name.StartsWith(y)) == null).
                        Where(x => !Initializer.Inspected.Contains(x.Name)).
                        Select(x => System.Reflection.Assembly.Load(x));
            }
        }

        protected override DependencyBuilder<Assembly>.Item CreateItem (Assembly key)
        {
            return new Item ();
        }
    }
#endif

    /// <summary>
    /// Extension of DependencyBuilder which facilities initialization of Items (nodes) found.
    /// Initialization is managed in such a way where multiple nodes may initialize at once, so long
    /// as no dependency collision occurs
    /// </summary>
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
                var  countMapper = new int[] 
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

        new abstract public class Item : _DependencyBuilderItem<T>
        {

            public bool IsInitialized
            {
                get { return initialized; }
                private set { initialized = value; }
            }

            public abstract string Name { get; }

            public Item()
            {
                ChildAdded += child =>
                {
                    var _child = (Item)child;

                    // if child is uninitialized, then be sure to add this to the uninitialized
                    // list for this item
                    if(!_child.initialized)
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

                DigEnded += Item_DigEnded;
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
                        if(direct)
                            loggerInit.Info("Initializing: " + Name + " (DIRECT)");

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
                                Initialize();

                                initializing = false;
                                IsInitialized = true;

                                if (Initialized != null)
                                    Initialized(this);
                            });
                        }
                        else
                        {
                            WaitForDependencies();

                            IsInitialized = true;
                            if (Initialized != null)
                                Initialized(this);
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
            /// Item is waiting on.  This method performs this task, and fires
            /// off any relevant "dependencies completed" events as well
            /// 
            /// Remember not all items receive an Initialize() call, but these
            /// items may still have dependencies - so even if ShouldInitialize
            /// returns FALSE, WaitForDependencies is still called
            /// </summary>
            protected virtual void WaitForDependencies() { }

            /// <summary>
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
            void Item_DigEnded(DependencyBuilder<T>.Item obj)
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
            public HashSet<Item> ChildrenUninitialized = new HashSet<Item>();

            /// <summary>
            /// Fired when this particular item and its T finishes initializing
            /// </summary>
            public event Action<Item> Initialized;
        }
    }



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
#if NET40
#define CUSTOM_THREADPOOL
#endif

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Fact.Extensions;
using Fact.Extensions.Factories;

#if CUSTOM_THREADPOOL
//using Castle.MicroKernel.Registration;
#endif

using System.Threading;
using System.Threading.Tasks;
using System.Reflection;

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


    /// <summary>
    /// TODO: Consolidate with Fact.Extensions.Factories.AggregateFactory
    /// </summary>
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



    /// <summary>
    /// TODO: Consolidate with Fact.Extensions.Factories.DelegateFactory
    /// </summary>
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


    public abstract class ChildSyncMeta<T> : DependencyBuilderNode<T>.Meta
    {
        public ChildSyncMeta(string key, DependencyBuilderNode<T> item) :
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


    public abstract class ChildAsyncMeta<T> : DependencyBuilderNode<T>.Meta
    {
        public ChildAsyncMeta(string key, DependencyBuilderNode<T> item) :
            base(key, item)
        {
            // when child is added, register child as something this item depends on
            // to reach satisfied state.  However, this is only tracked so that outside
            // processes know when the entire ChildAsyncMeta tree is done.  The tree
            // 
            item.ChildAdded += _item => EvalSatisfy2();

            item.DigEnded += HandleDigEnded;
        }

        void HandleDigEnded (DependencyBuilder<T>.Node obj)
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

				var children = Item.Children as ICollection<DependencyBuilder<T>.Node>;
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

    public abstract class UninitializingItem<T> : DependencyBuilderNode<T>
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

            public UninitializeMeta(string key, DependencyBuilderNode<T> item) : base(key, item) 
            {
                //item.ParentAdded += Handler;
            }
        }

        public UninitializingItem(T value) : base(value)
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


}
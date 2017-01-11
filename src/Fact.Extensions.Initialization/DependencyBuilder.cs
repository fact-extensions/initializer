using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

namespace Fact.Extensions.Initialization
{
    public class DependencyBuilder
    {
    }

    public abstract class DependencyBuilder<T> : DependencyBuilder
    {
        static readonly ILogger logger = LogManager.CreateLogger<DependencyBuilder<T>>();

        /// <summary>
        /// This class represents a node in the dependency tree
        /// </summary>
        public abstract class Node
        {
            readonly public T value;

            public Node(T value)
            {
                this.value = value;
            }

            LinkedList<Node> children = new LinkedList<Node>();
            LinkedList<Node> parents = new LinkedList<Node>();

            public IEnumerable<Node> Children { get { return children; } }
            public IEnumerable<Node> Parents { get { return parents; } }

            /// <summary>
            /// Occurs when child is initially added to this node, but before it is itself dug into
            /// </summary>
            public event Action<Node> ChildAdded;
            public event Action<Node> ParentAdded;

            /// <summary>
            /// When the dig phase for this item ends, fire this
            /// End of dig phase should have children and parents
            /// fully built out
            /// </summary>
            public event Action<Node> DigEnded;

            public void AddChild(Node child)
            {
                children.AddLast(child);

                if (ChildAdded != null)
                    ChildAdded(child);
            }


            public void AddParent(Node parent)
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

            /// <summary>
            /// Indicates whether we should dig into this node
            /// </summary>
            public virtual bool ShouldDig { get { return true; } }
        }

        Dictionary<object, Node> lookup = new Dictionary<object, Node>();

        /// <summary>
        /// Denotes which nodes have already been inspected, and won't dig into them
        /// if they are encountered again in the tree
        /// </summary>
        HashSet<T> alreadyInspected = new HashSet<T>();

        /// <summary>
        /// Fired right when an item gets created, before any Dig or Start processing occurs
        /// </summary>
        public event Action<Node> NodeCreated;

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
        /// Acquires the already-existing Node for the dependency graph,
        /// otherwise creates it and tracks it for next time
        /// </summary>
        /// <param name="key"></param>
        /// <returns></returns>
        protected Node GetValue(T key)
        {
            Node node;
            object _key = GetKey(key);

            if (!lookup.TryGetValue(_key, out node))
            {
                node = CreateNode(key);

                NodeCreated?.Invoke(node);

                node.Start();

                lookup.Add(_key, node);
            }

            return node;
        }


        /// <summary>
        /// Factory method for Items (nodes)
        /// </summary>
        /// <param name="key"></param>
        /// <returns></returns>
        protected abstract Node CreateNode(T key);

        /// <summary>
        /// Dig into children, recursively calling Dig(T)
        /// Low level dig method, for internal/reuse
        /// </summary>
        /// <param name="current"></param>
        /// <param name="node"></param>
        protected void DigChildren(Node node, IEnumerable<T> children)
        {
            foreach (var child in children)
            {
                var childNode = GetValue(child);

                // each child is queried to see if we should dig further into it
                if (childNode.ShouldDig)
                {
                    // track that this node indeed has a child of dig interest, and
                    // fire any listening callbacks as well
                    node.AddChild(childNode);
                    Dig(child, node.value);
                }
            }
        }

        /// <summary>
        /// Dig all the way to the bottom, then build from the bottom up
        /// </summary>
        /// <param name="current"></param>
        public Node Dig(T current, T parent)
        {
            // FIX: kludgey
            alreadyInspected.Add(parent);

            var node = GetValue(current);

            if (parent != null)
                node.AddParent(GetValue(parent));

            if (alreadyInspected.Contains(current))
                return node;

            try
            {
                var children = node.GetChildren();
#if DEBUG
                children = children.ToArray();
#endif
                DigChildren(node, children);
            }
            catch (Exception e)
            {
                logger.LogDebug("DependencyBuilder::Dig failure on inspecting children of: " + RetrieveDescription(current));
                logger.LogDebug("DependencyBuilder::Dig exception: " + e.Message);
                throw;
            }

            alreadyInspected.Add(current);
            node.DoDigEnded();
            return node;
        }


        protected virtual string RetrieveDescription(T value)
        {
            return value.ToString();
        }
    }

    public abstract class DependencyBuilderNode<T> : DependencyBuilder<T>.Node
    {
        public DependencyBuilderNode(T value) : base(value) { }

        public abstract class Meta
        {
            public HashSet<DependencyBuilderNode<T>> Dependencies = new HashSet<DependencyBuilderNode<T>>();
            public bool IsSatisfied;
            public readonly DependencyBuilderNode<T> Item;

            /// <summary>
            /// This is the core method to Satisfy any underlying dependencies
            /// </summary>
            protected abstract void Satisfy();

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
                        if (BeginSatisfy != null)
                            BeginSatisfy(this);

                        if (IsAsync)
                        {
                            satisfyTask = Task.Factory.StartNew(() =>
                            {
                                Satisfy();

                                if (Satisfied != null)
                                    Satisfied(this);
                            });
                        }
                        else
                            Satisfy();
                    }

                    // This is fired on the async handler when in async mode
                    if (!IsAsync && Satisfied != null)
                        Satisfied(this);
                }
            }

            public readonly string Key;

            public Meta(string key, DependencyBuilderNode<T> item)
            {
                Key = key;
                Item = item;
            }


            /// <summary>
            /// TODO: Document what this method does
            /// </summary>
            /// <param name="_item"></param>
            public void Handler(DependencyBuilder<T>.Node _item)
            {
                var item = (DependencyBuilderNode<T>)_item;
                var itemMeta = item.Dependencies[Key];

                // If _item has not satisfied its own dependencies, then
                // we must depend on it since dependencies cascade
                if (!itemMeta.IsSatisfied)
                {
                    Dependencies.Add(item);

                    Satisfied += __item =>
                    {
                        Dependencies.Remove(item);
                        EvalSatisfy();
                    };
                }
            }

            public virtual void Start() { }
        }

        Dictionary<string, Meta> Dependencies = new Dictionary<string, Meta>();

        /// <summary>
        /// Add this to the dependency list.  Next-generation code, not fully functional yet
        /// </summary>
        /// <param name="meta"></param>
        protected void Add(Meta meta)
        {
            Dependencies.Add(meta.Key, meta);
        }
    }
}

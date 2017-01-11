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
        /// <summary>
        /// Represents set of nodes which are initializing asynchronously
        /// </summary>
		HashSet<Node> AsyncInitializing = new HashSet<Node>();

        protected void AddAsyncNode(Node node)
        {
            lock(AsyncInitializing)
            {
                AsyncInitializing.Add(node);
            }
        }


        protected void RemoveAsyncNode(Node node)
        {
            lock(AsyncInitializing)
            {
                AsyncInitializing.Remove(node);
            }
        }

        /*
        public abstract new class Node : InitializingDependencyBuilder<T>.Node
        {
            public Node(T value) : base(value) { }

            /// <summary>
            /// Called when async init portion begins
            /// </summary>
            public event Action<Node> AsyncBegin;
            /// <summary>
            /// Called when async init portion is complete
            /// </summary>
            public event Action<Node> AsyncEnd;
        }*/
    }
}

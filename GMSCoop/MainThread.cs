using System;
using System.Collections.Concurrent;

namespace GMSCoop
{
    /// <summary>Runs callbacks from worker threads on the Unity main thread.</summary>
    internal static class MainThread
    {
        private static readonly ConcurrentQueue<Action> Queue = new ConcurrentQueue<Action>();

        internal static void Run(Action a) => Queue.Enqueue(a);

        /// <summary>Call once per frame from Plugin.Update.</summary>
        internal static void Drain()
        {
            while (Queue.TryDequeue(out Action a))
            {
                try { a(); }
                catch (Exception ex) { Plugin.Log.LogError("Main thread task failed: " + ex); }
            }
        }
    }
}

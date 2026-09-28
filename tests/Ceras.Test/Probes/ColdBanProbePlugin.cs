// The plugin assembly that ColdBanProbe.cs loads into collectible AssemblyLoadContexts.
// NOT compiled into Ceras.Test: BanVerdictCacheTests emits it and hands the path to the child process,
// because a collectible context can only be demonstrated with types loaded from a real file - a type
// that already exists in the test process lives in the default context and can demonstrate nothing.
//
// The shapes cover every way a Type can refer to another Type, so the probe can show that parking any
// of them in a static field keeps the context alive: Plain directly, Outer<T>.Inner nested inside a
// *generic* (so its declaring type is a composite too), and Enumerable to check a ban verdict.
namespace PluginNs
{
    public class Plain { public int A; }

    public class Outer<T>
    {
        public class Inner { public int B; }
        public T Value;
    }

    // Inherits the IEnumerator ban, so "banned" here does not depend on Ceras knowing the type.
    public class Enumerable : System.Collections.IEnumerator
    {
        public object Current => null;
        public bool MoveNext() => false;
        public void Reset() { }
    }
}

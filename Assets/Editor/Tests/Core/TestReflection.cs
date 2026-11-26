using System;
using System.Linq;
using System.Reflection;

namespace Game.EditorTests
{
    // Private scene-owner methods may be overloaded: pick the one the arguments fit.
    internal static class TestReflection
    {
        internal static MethodInfo FindMethod(Type type, string name, BindingFlags flags, object[] arguments)
        {
            arguments = arguments ?? Array.Empty<object>();
            MethodInfo[] fits = type.GetMethods(flags).Where(m => m.Name == name && Fits(m, arguments)).ToArray();
            if (fits.Length > 1)
                throw new AmbiguousMatchException($"{type.Name}.{name}: {fits.Length} overloads fit the arguments.");
            return fits.Length == 1 ? fits[0] : null;
        }

        private static bool Fits(MethodInfo method, object[] arguments)
        {
            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length != arguments.Length) return false;
            for (int i = 0; i < parameters.Length; i++)
            {
                Type p = parameters[i].ParameterType;
                if (arguments[i] == null ? p.IsValueType && Nullable.GetUnderlyingType(p) == null
                    : !p.IsInstanceOfType(arguments[i]))
                    return false;
            }
            return true;
        }
    }
}

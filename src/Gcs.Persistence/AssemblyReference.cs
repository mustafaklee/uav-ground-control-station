using System.Reflection;

namespace Gcs.Persistence;

/// <summary>
/// Marker used to locate this assembly (architecture tests, DI scanning) without referencing a concrete type.
/// </summary>
public static class AssemblyReference
{
    public static readonly Assembly Assembly = typeof(AssemblyReference).Assembly;
}

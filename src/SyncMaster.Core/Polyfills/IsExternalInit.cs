// Polyfill: enables 'init' property setters on .NET Framework / netstandard2.0
// The type System.Runtime.CompilerServices.IsExternalInit is provided by the
// runtime in .NET 5+ but must be declared manually for older targets.
namespace System.Runtime.CompilerServices;

internal static class IsExternalInit { }

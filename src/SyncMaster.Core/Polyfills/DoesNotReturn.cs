// Polyfill: enables [DoesNotReturn] on .NET Framework 4.8.
// The type System.Diagnostics.CodeAnalysis.DoesNotReturnAttribute is provided
// by the runtime in .NET Core 3.0+ but must be declared manually for older targets.
namespace System.Diagnostics.CodeAnalysis;

[AttributeUsage(AttributeTargets.Method, Inherited = false)]
internal sealed class DoesNotReturnAttribute : Attribute { }

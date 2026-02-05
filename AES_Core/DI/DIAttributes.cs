using System;

namespace AES_Core.DI;

/// <summary>
/// Marks a class to be automatically registered with the dependency
/// injection system when the source-generator emits registration code.
/// The generator will produce registration helpers that wire types
/// annotated with this attribute into the container.
/// </summary>
[Serializable]
[AttributeUsage(AttributeTargets.Class)]
public class AutoRegisterAttribute : Attribute
{
}

/// <summary>
/// Marks a field or property to be resolved automatically from the
/// DI container when the generated partial initialization method runs.
/// Generated code will attempt to resolve and assign annotated members
/// (using <c>ResolveOptional</c>) to avoid throwing when a dependency
/// is not present.
/// </summary>
[Serializable]
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public class AutoResolveAttribute : Attribute
{
}

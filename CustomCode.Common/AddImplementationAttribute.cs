namespace CustomCode.Common;

public class AddImplementationAttribute : Attribute;

[AttributeUsage(AttributeTargets.Class)]
public class AddImplementationAttribute<TInterface> : AddImplementationAttribute;
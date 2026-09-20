namespace Sample;

public static class Demo
{
    public static Guid Empty() => new Guid();

    public static Guid Implicit()
    {
        Guid g = new();
        return g;
    }

    public static Guid FromBytes(byte[] b) => new Guid(b);

    public static string NoHoles() => $"no holes here";

    public static string Escaped() => $"braces {{kept}}";

    public static string WithHole(int x) => $"value {x}";
}

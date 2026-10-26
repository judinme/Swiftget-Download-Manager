using FsCheck.Xunit;
using Xunit;

namespace SwiftGet.Engine.PropertyTests;

public class PropertyTestSmoke
{
    [Property]
    public bool IntegerAddition_IsCommutative(int a, int b)
    {
        return a + b == b + a;
    }
}

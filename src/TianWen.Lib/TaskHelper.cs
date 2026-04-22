using System;
using System.Threading.Tasks;

namespace TianWen.Lib;

public static class TaskHelper
{
    public static async Task<TResult> Let<T, TResult>(this T self, Func<T, Task<TResult>> func) => await func(self);
}

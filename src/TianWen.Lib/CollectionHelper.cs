using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Text;
using System.Web;

namespace TianWen.Lib;

public static class CollectionHelper
{
    public static string ToQueryString(this NameValueCollection @this)
    {
        var sb = new StringBuilder();

        foreach (string name in @this)
        {
            if (sb.Length > 0)
            {
                sb.Append('&');
            }
            sb.Append(name).Append('=').Append(HttpUtility.UrlEncode(@this[name]));
        }

        return sb.ToString();
    }

    public static void CopyTo<T>(this IReadOnlyList<T> @this, T[] array, int arrayIndex)
    {
        if (@this is ICollection<T> collection)
        {
            collection.CopyTo(array, arrayIndex);
        }
        else
        {
            for (var i = 0; i < @this.Count; i++)
            {
                array[arrayIndex + i] = @this[i];
            }
        }
    }

    public static void CopyTo<T>(this IReadOnlyCollection<T> @this, T[] array, int arrayIndex)
    {
        if (@this is ICollection<T> collection)
        {
            collection.CopyTo(array, arrayIndex);
        }
        else
        {
            var i = arrayIndex;
            foreach (T elem in @this)
            {
                array[i++] = elem;
            }
        }
    }

    public static int IndexOf<T>(this IReadOnlyList<T> @this, T item)
    {
        if (@this is IList<T> list)
        {
            return list.IndexOf(item);
        }
        else
        {
            for (var i = 0; i < @this.Count; i++)
            {
                if (@this[i]?.Equals(item) == true)
                {
                    return i;
                }
            }

            return -1;
        }
    }

    public static TValue GetOrAdd<TKey, TValue>(this IDictionary<TKey, TValue> @this, TKey key, TValue @new) => @this.TryGetValue(key, out var value) ? value : (@this[key] = @new);
}

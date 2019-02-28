﻿using System.Reflection;

namespace Vostok.Configuration.Binders.Extensions
{
    internal static class PropertyInfoExtensions
    {
        public static void ForceSetValue(this PropertyInfo property, object obj, object value)
        {
            if (property.CanWrite)
            {
                property.SetValue(obj, value);
                return;
            }

            var backingField = property.DeclaringType?.GetField($"<{property.Name}>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            if (backingField != null)
                backingField.SetValue(obj, value);
        }

        public static bool IsAbstract(this PropertyInfo propertyInfo)
        {
            var isGetterAbs = propertyInfo.GetMethod?.IsAbstract;
            var isSetterAbs = propertyInfo.SetMethod?.IsAbstract;
            return isGetterAbs.HasValue && isGetterAbs.Value || isSetterAbs.HasValue && isSetterAbs.Value;
        }
    }
}
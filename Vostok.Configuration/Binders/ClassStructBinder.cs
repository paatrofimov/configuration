﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SimpleInjector;
using Vostok.Configuration.Abstractions.Attributes;
using Vostok.Configuration.Abstractions.SettingsTree;
using Vostok.Configuration.Binders.Extensions;
using Vostok.Configuration.Binders.Results;
using Vostok.Configuration.Extensions;
using Vostok.Configuration.Helpers;

namespace Vostok.Configuration.Binders
{
    internal class ClassStructBinder<T> : ISafeSettingsBinder<T>, INullValuePolicy
    {
        private readonly ISettingsBinderProvider binderProvider;

        public ClassStructBinder(ISettingsBinderProvider binderProvider) =>
            this.binderProvider = binderProvider;

        public SettingsBindingResult<T> Bind(ISettingsNode settings)
        {
            if (!settings.IsNullOrMissing() && !(settings is ObjectNode))
                return SettingsBindingResult.NodeTypeMismatch<T>(settings);

            return SettingsBindingResult.Catch(() => BindInternal(settings));
        }

        public bool IsNullValue(ISettingsNode node)
        {
            if (node.IsNullValue())
                return true;

            if (typeof(T).IsValueType)
                return false;

            return node is ValueNode valueNode && valueNode.Value?.ToLower() == "null";
        }

        private static bool IsRequired(MemberInfo member, bool requiredByDefault)
        {
            if (requiredByDefault)
                return member.GetCustomAttribute<OptionalAttribute>() == null;

            return member.GetCustomAttribute<RequiredAttribute>() != null;
        }

        private static bool ShouldSkipMemberOfAbstractType(ISafeSettingsBinder<object> binder, Type type)
        {
            if (!type.IsAbstract && !type.IsInterface)
                return false;

            return binder is IBinderWrapper wrapper && wrapper.BinderType.IsClosedTypeOf(typeof(ClassStructBinder<>));
        }

        private SettingsBindingResult<T> BindInternal(ISettingsNode settings)
        {
            var type = typeof(T);
            var instance = Activator.CreateInstance(type);

            using (SecurityHelper.StartSecurityScope(type))
            {
                var requiredByDefault = type.GetCustomAttribute<RequiredByDefaultAttribute>() != null;

                var errors = new List<SettingsBindingError>();

                foreach (var field in type.GetInstanceFields())
                {
                    var result = GetValue(field.FieldType, field, IsRequired(field, requiredByDefault), settings, field.GetValue(instance));
                    errors.AddRange(result.Errors.ForProperty(field.Name));
                    if (!result.Errors.Any())
                        field.SetValue(instance, result.Value);
                }

                foreach (var property in type.GetInstanceProperties())
                {
                    var result = GetValue(property.PropertyType, property, IsRequired(property, requiredByDefault), settings, property.GetValue(instance));
                    errors.AddRange(result.Errors.ForProperty(property.Name));
                    if (!result.Errors.Any())
                        property.ForceSetValue(instance, result.Value);
                }

                if (errors.Any())
                    return SettingsBindingResult.Errors<T>(errors);

                return SettingsBindingResult.Success((T)instance);
            }
        }

        private SettingsBindingResult<object> GetValue(Type type, MemberInfo member, bool isRequired, ISettingsNode settings, object defaultValue)
        {
            using (SecurityHelper.StartSecurityScope(member))
            {
                if (!member.TryObtainBindByBinder<object>(true, out var binder))
                    binder = binderProvider.CreateFor(type);

                if (binder == null)
                    return SettingsBindingResult.BinderNotFound<object>(type);

                if (ShouldSkipMemberOfAbstractType(binder, type))
                    return SettingsBindingResult.Success(defaultValue);

                var value = settings?[member.Name];
                if (!value.IsNullOrMissing(binder))
                    return binder.Bind(value);

                if (isRequired)
                    return SettingsBindingResult.RequiredPropertyIsNull<object>(member.Name);

                return SettingsBindingResult.Success(value.IsMissing() ? defaultValue : null);
            }
        }
    }
}
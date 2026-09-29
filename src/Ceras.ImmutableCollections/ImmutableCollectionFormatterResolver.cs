using System;
using System.Collections.Generic;
using System.Text;

namespace Ceras.ImmutableCollections
{
    using System.Collections.Immutable;
    using Formatters;
    using Resolvers;

    public sealed class ImmutableCollectionFormatterResolver : IFormatterResolver
    {
        readonly Dictionary<Type, Type> _typeToFormatterType = new Dictionary<Type, Type>
        {
            [typeof(ImmutableArray<>)] = typeof(ImmutableArrayFormatter<>),
            [typeof(ImmutableDictionary<,>)] = typeof(ImmutableDictionaryFormatter<,>),
            [typeof(ImmutableHashSet<>)] = typeof(ImmutableHashSetFormatter<>),
            [typeof(ImmutableList<>)] = typeof(ImmutableListFormatter<>),
            [typeof(ImmutableQueue<>)] = typeof(ImmutableQueueFormatter<>),
            [typeof(ImmutableSortedDictionary<,>)] = typeof(ImmutableSortedDictionaryFormatter<,>),
            [typeof(ImmutableSortedSet<>)] = typeof(ImmutableSortedSetFormatter<>),
            [typeof(ImmutableStack<>)] = typeof(ImmutableStackFormatter<>),
        };

        public IFormatter GetFormatter(Type type)
        {
            if(type.Assembly != typeof(System.Collections.Immutable.ImmutableArray).Assembly)
                return null;

            if(type.IsGenericType)
                if (_typeToFormatterType.TryGetValue(type.GetGenericTypeDefinition(), out var formatterType))
                {
                    var genericArgs = type.GetGenericArguments();
                    formatterType = formatterType.MakeGenericType(genericArgs);
                    return (IFormatter)Activator.CreateInstance(formatterType);
                }

            return null;
        }

        /// <summary>
        /// Approves this assembly's formatter types for a robust context.
        /// <para>
        /// These eight resolvers answer through <see cref="CerasConfig.OnResolveFormatter"/> — a user
        /// door, and one a robust context now guards: a formatter selected by a resolver cannot be
        /// recorded in a manifest that is already sealed and baked by the time the resolver is asked, so
        /// an unapproved answer is refused rather than written as bytes no schema describes. See
        /// <c>Ceras.Format.RobustResolverPolicy</c>.
        /// </para>
        /// <para>
        /// They are approved because their bytes are described already, not because they are ours: each
        /// one is an <c>IFormatter&lt;T&gt;</c> for a single immutable collection type, and the context's
        /// manifest holds a <c>GENERIC</c> structural entry for that type — a <c>List&lt;&gt;</c> or
        /// <c>Dictionary&lt;,&gt;</c> shape with its argument types — which is exactly what the payload is.
        /// That is also what makes the approval safe rather than a blanket exemption: a type closed over
        /// one value type can only ever be installed for that type, so approving the family cannot make
        /// anything else writable.
        /// </para>
        /// <para>
        /// Registering the <em>open generic definitions</em> keeps this compile-checked: a renamed or
        /// deleted formatter fails to build here rather than silently losing its approval and starting to
        /// be refused at runtime. The identity compared on the other side is the runtime
        /// <see cref="Type"/> itself, never a name or a namespace — a caller can declare a class with
        /// either, and cannot become one of these.
        /// </para>
        /// </summary>
        static ImmutableCollectionFormatterResolver()
        {
            Format.RobustResolverPolicy.RegisterApprovedFormatterType(typeof(ImmutableArrayFormatter<>));
            Format.RobustResolverPolicy.RegisterApprovedFormatterType(typeof(ImmutableDictionaryFormatter<,>));
            Format.RobustResolverPolicy.RegisterApprovedFormatterType(typeof(ImmutableHashSetFormatter<>));
            Format.RobustResolverPolicy.RegisterApprovedFormatterType(typeof(ImmutableListFormatter<>));
            Format.RobustResolverPolicy.RegisterApprovedFormatterType(typeof(ImmutableQueueFormatter<>));
            Format.RobustResolverPolicy.RegisterApprovedFormatterType(typeof(ImmutableSortedDictionaryFormatter<,>));
            Format.RobustResolverPolicy.RegisterApprovedFormatterType(typeof(ImmutableSortedSetFormatter<>));
            Format.RobustResolverPolicy.RegisterApprovedFormatterType(typeof(ImmutableStackFormatter<>));
        }

        public static void ApplyToConfig(CerasConfig config)
        {
            var immutableResolver = new ImmutableCollectionFormatterResolver();
            config.OnResolveFormatter.Add((c, t) => immutableResolver.GetFormatter(t));
        }
    }

    public static class CerasImmutableExtension
    {
        public static void UseImmutableFormatters(this CerasConfig config)
        {
            ImmutableCollectionFormatterResolver.ApplyToConfig(config);
        }
    }
}

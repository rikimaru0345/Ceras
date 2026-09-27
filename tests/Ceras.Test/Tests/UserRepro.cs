using System;
using System.Collections.Generic;
using Xunit;
using Ceras;
using Ceras.Format;
using Ceras.Helpers;
using Ceras.Formatters;

namespace Ceras.Test
{
    public class ReproTests : TestBase
    {
        [Fact]
        public void Repro_PreserveNullInList()
        {
            var robust = new CerasRobustSerializer(config =>
            {
                // Manually register types
                config.SchemaRegistry.Register(typeof(ReproGame), CreateSchema(typeof(ReproGame)));
                config.SchemaRegistry.Register(typeof(ILogicalRepro), CreateSchema(typeof(ILogicalRepro)));
                config.SchemaRegistry.Register(typeof(ConcreteRepro), CreateSchema(typeof(ConcreteRepro)));
                config.SchemaRegistry.Seal();
            });

            var game = new ReproGame();
            game.Entities.Add(new ConcreteRepro { Value = 1 });
            game.Entities.Add(null); // Explicit null
            game.Entities.Add(new ConcreteRepro { Value = 2 });

            // 1. Save
            var data = robust.Save(game, 1);

            // 2. Load
            var loadedGame = robust.Load<ReproGame>(data, 1);

            // 3. Verify
            Assert.NotNull(loadedGame);
            Assert.Equal(3, loadedGame.Entities.Count);
            Assert.NotNull(loadedGame.Entities[0]);
            Assert.Null(loadedGame.Entities[1]);
            Assert.NotNull(loadedGame.Entities[2]);
        }

        static SchemaTypeInfo CreateSchema(Type type)
        {
            if (type.IsInterface)
            {
                return new SchemaTypeInfo(
                    stableName: type.FullName ?? type.Name,
                    previousTypeNames: Array.Empty<string>(),
                    kind: SchemaTypeKind.Class,
                    fields: Array.Empty<SchemaFieldInfo>(),
                    fixedByteSize: 0);
            }

            var fields = new List<SchemaFieldInfo>();
            foreach (var f in type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
            {
                fields.Add(new SchemaFieldInfo(f.Name, new string[0], f.FieldType.FullName ?? f.FieldType.Name, f.FieldType.IsValueType || f.FieldType.IsSealed));
            }

            return new SchemaTypeInfo(
                stableName: type.FullName ?? type.Name,
                previousTypeNames: Array.Empty<string>(),
                kind: type.IsValueType ? SchemaTypeKind.Struct : SchemaTypeKind.Class,
                fields: fields.ToArray(),
                fixedByteSize: 0);
        }
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class ReproGame
    {
        public List<ILogicalRepro> Entities = new();
    }

    [CerasContext("Tests")]
    [CerasSerializable("ILogicalRepro")]
    public interface ILogicalRepro
    {
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class ConcreteRepro : ILogicalRepro
    {
        public int Value;
        public object Self;
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class ReproContainer : ILogicalRepro
    {
        public List<ILogicalRepro> Entities = new();
    }
}

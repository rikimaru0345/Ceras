using System;
using System.Collections.Generic;
using Xunit;
using Ceras;
using Ceras.Format;
using Ceras.Helpers;
using Ceras.Formatters;
using Ceras.Generated;

namespace Ceras.Test
{
    public class CircularCtorRepro : TestBase
    {
        /*
         * THE "WAIT-FOR-ME" CYCLE (Circular Reference + [CerasConstructor])
         * 
         * This test reproduces a subtle issue where objects using [CerasConstructor] can result in 'null' 
         * back-references if encountered in a specific order during deserialization.
         * 
         * Scenario:
         * 1. Deserializer encounters ChildEntity (A) for the first time.
         * 2. Since (A) has a [CerasConstructor], Ceras enters "Phase 1": reading constructor parameters.
         * 3. One parameter is ParentEntity (B). Ceras pauses (A) and descends into (B).
         * 4. (B) has a normal constructor, so it is instantiated immediately and added to the ObjectCache.
         * 5. (B) starts deserializing its members, including its 'Children' list.
         * 6. The list contains ChildEntity (A) again. Ceras sees (A) is already in the cache (as a RefProxy).
         * 7. PROBLEM: Because (A) is still in Phase 1 (waiting for its constructor to return), the 
         *    RefProxy.Value is still 'null'.
         * 8. (B) puts this 'null' into its list and finishes.
         * 9. Finally, (A) finishes Phase 1, calls its constructor with (B), and sets its instance.
         * 10. RESULT: (A) has a reference to (B), but (B) is stuck with a 'null' reference to (A).
         */
        [Fact]
        public void Repro_CircularReference_With_CerasConstructor_Becomes_Null_In_List()
        {
            CerasSerializer.ClearFormatterConstructedTypes();

            var robust = new CerasRobustSerializer(config =>
            {
                config.DefaultTargets = TargetMember.AllFields;
                // Manual registration for robust mode in tests
                config.SchemaRegistry.Register(typeof(GameRoot), CreateSchema(typeof(GameRoot)));
                config.SchemaRegistry.Register(typeof(ParentEntity), CreateSchema(typeof(ParentEntity)));
                config.SchemaRegistry.Register(typeof(ChildEntity), CreateSchema(typeof(ChildEntity)));
            });

            var game = new GameRoot();
            var parent = new ParentEntity();
            var child = new ChildEntity(parent);

            parent.Children.Add(child);

            // CRITICAL: We add the child to 'AllEntities' FIRST.
            // When deserializing, 'AllEntities[0]' will start deserializing the child.
            // The child will then trigger deserialization of 'Parent'.
            // The 'Parent' will then trigger deserialization of its 'Children' list.
            // The 'Children' list will encounter the 'Child' again (back-reference).
            game.AllEntities.Add(child);
            game.Parents.Add(parent);

            // 1. Save
            var data = robust.Save(game, 1);

            // 2. Load
            var loadedGame = robust.Load<GameRoot>(data, 1);

            // 3. Verify
            Assert.NotNull(loadedGame);
            Assert.Single(loadedGame.Parents);
            Assert.Single(loadedGame.AllEntities);
            
            var loadedParent = (ParentEntity)loadedGame.Parents[0];
            var loadedChildFromList = (ChildEntity)loadedGame.AllEntities[0];
            
            Assert.NotNull(loadedParent);
            Assert.Single(loadedParent.Children);
            
            var loadedChildFromParent = (ChildEntity)loadedParent.Children[0];

            // In the user's case, these were null!
            Assert.NotNull(loadedChildFromList);
            Assert.NotNull(loadedChildFromParent);
            
            Assert.Same(loadedChildFromList, loadedChildFromParent);
            Assert.Same(loadedParent, loadedChildFromParent.Parent);
        }

        [Fact]
        public void Repro_CircularReference_With_CerasConstructor_SourceGenerator()
        {
            var robust = new CerasRobustSerializer(config =>
            {
                config.DefaultTargets = TargetMember.AllFields;
                config.AllowReflectionFallback = false;
                config.UseGeneratedFormatters();
                // Manual registration for robust mode in tests
                config.SchemaRegistry.Register(typeof(GameRoot), CreateSchema(typeof(GameRoot)));
                config.SchemaRegistry.Register(typeof(ParentEntity), CreateSchema(typeof(ParentEntity)));
                config.SchemaRegistry.Register(typeof(ChildEntity), CreateSchema(typeof(ChildEntity)));
            });

            var game = new GameRoot();
            var parent = new ParentEntity();
            var child = new ChildEntity(parent);

            parent.Children.Add(child);

            game.AllEntities.Add(child);
            game.Parents.Add(parent);

            // 1. Save
            var data = robust.Save(game, 1);

            // 2. Load
            var loadedGame = robust.Load<GameRoot>(data, 1);

            // 3. Verify
            Assert.NotNull(loadedGame);
            var loadedParent = (ParentEntity)loadedGame.Parents[0];
            var loadedChildFromList = (ChildEntity)loadedGame.AllEntities[0];
            
            Assert.NotNull(loadedParent);
            Assert.Single(loadedParent.Children);
            
            var loadedChildFromParent = (ChildEntity)loadedParent.Children[0];

            Assert.NotNull(loadedChildFromList);
            Assert.NotNull(loadedChildFromParent);
            
            Assert.Same(loadedChildFromList, loadedChildFromParent);
            Assert.Same(loadedParent, loadedChildFromParent.Parent);
        }

        static SchemaTypeInfo CreateSchema(Type type)
        {
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

        [CerasSerializable]
        public class GameRoot
        {
            // Using 'object' or interfaces to force usage of ReflectionDeserializationHelper
            public List<object> AllEntities = new();
            public List<object> Parents = new();
        }

        [CerasSerializable]
        public class ParentEntity
        {
            public List<object> Children = new();
        }

        [CerasSerializable]
        public class ChildEntity
        {
            [Include]
            public ParentEntity Parent;

            [CerasConstructor]
            public ChildEntity(ParentEntity parent)
            {
                Parent = parent;
            }
        }
    }
}

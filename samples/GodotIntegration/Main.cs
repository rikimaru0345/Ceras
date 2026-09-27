using Godot;
using System;
using Ceras;
using Ceras.Generated;

namespace GodotIntegration
{
    // A type with a private field and a private parameterized constructor. The generated robust
    // formatter must reach both through [UnsafeAccessor], and must invoke the constructor.
    [CerasSerializable]
    public class SecretHolder
    {
        private readonly int _secret;
        private string _label;

        private SecretHolder(int secret)
        {
            _secret = secret;
            _label = "constructed";
        }

        public static SecretHolder Create(int secret) => new SecretHolder(secret);

        public int Secret => _secret;
        public string Label => _label;
    }

    // A generic type with private members and a parameterized constructor. Closed instantiations
    // are generated with a nested generic accessor helper matching the target's type parameters.
    [CerasSerializable]
    public class Box<T>
    {
        private T _value;
        private int _ctorRuns;

        public Box(T value)
        {
            _value = value;
            _ctorRuns = 1;
        }

        public T Value => _value;
        public int CtorRuns => _ctorRuns;
    }

    [CerasSerializable]
    public class SaveModel
    {
        public SecretHolder Holder;
        public Box<int> IntBox;
        public Box<string> StringBox;
    }

    public partial class Main : Node
    {
        public override void _Ready()
        {
            try
            {
                var serializer = new CerasRobustSerializer(c => c.UseGeneratedFormatters());

                var model = new SaveModel
                {
                    Holder = SecretHolder.Create(1337),
                    IntBox = new Box<int>(42),
                    StringBox = new Box<string>("hello")
                };

                var bytes = serializer.Save(model, 1);
                var clone = new CerasRobustSerializer(c => c.UseGeneratedFormatters()).Load<SaveModel>(bytes, 1);

                if (clone.Holder.Secret != 1337) throw new Exception("private field did not round-trip");
                if (clone.Holder.Label != "constructed") throw new Exception("private ctor did not run");
                if (clone.IntBox.Value != 42) throw new Exception("generic int member did not round-trip");
                if (clone.StringBox.Value != "hello") throw new Exception("generic string member did not round-trip");
                if (clone.IntBox.CtorRuns != 1) throw new Exception("generic ctor did not run");

                GD.Print("CERAS_GODOT_INTEGRATION_OK");
                GetTree().Quit(0);
            }
            catch (Exception e)
            {
                GD.PrintErr("CERAS_GODOT_INTEGRATION_FAIL: " + e);
                GetTree().Quit(1);
            }
        }
    }
}

using System;
using System.Threading;
using System.IO;
using TracerX;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;

namespace Sample
{
    // Demonstrate basic features of the TracerX logger.
    class Program
    {
        // Declare a Logger instance for use by this class.
        private static readonly Logger Log = Logger.GetLogger("Program");

        // Just one way to initialize TracerX.
        private static bool LogFileOpened = InitLogging();

        // Initialize the TracerX logging system.
        private static bool InitLogging()
        {
            // Give threads a name when feasible.  Either of 
            // the following methods work (only one is needed).
            // The first is "safer", but only makes the name 
            // known to TracerX.
            Logger.ThreadName = "Main Thread";
            Thread.CurrentThread.Name = "Main Thread";

            // This is optional, but you can apply configuration
            // settings from an XML file.
            Logger.Xml.Configure("LoggerConfig.xml");

            // This override some of settings loaded from LoggerConfig.xml.
            Logger.DefaultBinaryFile.Name = "SampleLog";
            Logger.DefaultBinaryFile.MaxSizeMb = 10;
            Logger.DefaultBinaryFile.CircularStartSizeKb = 1;

            // Open the output file.
            return Logger.DefaultBinaryFile.Open();
        }

        static void Main(string[] args)
        {
            Log.Debug("A message logged at stack depth = 0.");

            using (Log.InfoCall())
            {
                try
                {
                    Log.Info("This Logger's current TraceLevel is ", Log.BinaryFileTraceLevel);
                    Log.Info("A string");
                    Log.Info("A message \nwith multiple \nembedded \nnewlines.");
                    Log.Debug("The time is now ", DateTime.Now);
                    Log.DebugFormat("The UTC time is {0:HH:mm:ss.ffffff} ", DateTime.UtcNow);
                    Log.Info(@"~!@#$%^&*()_+{}|:”<>?/.,;’[]\=-±€£¥√∫©®™¬¶Ω∑");

                    Log.Info("Starting a Task/thread with Helper.Foo().");
                    Task t = Task.Factory.StartNew(Helper.Foo);

                    Log.Info("Test logging some lambda expressions.");
                    TestLabmdas("Value of parameter");

                    Log.Info("TracerX supports a stack depth of 255.  Test going even deeper with a recursive method.");
                    Recurse(0, 260);

                    Log.Info("Waiting for the Helper.Foo() Task to end.");
                    t.Wait();
                }
                 catch (Exception ex)
                {
                    Log.Error("Exception in Main: ", ex);
                }
            }

            Log.Debug("Another message logged at stack depth = 0.");
        }

        static void TestLabmdas(string parameter)
        {
            TestClass instance = new TestClass();
            string localVar = "Value of localVar";
            int anIntegerWithAnUnusuallyLongName = 1234;

            // The logger takes one or more lambda expressions and constructs a string
            // containing both the body of the expression and its value.  For example,
            //    Logger.PrtVar(() => localVar)
            // will return 
            //    localVar = "Value of localVar"
            // The body of the expression can be just about anything that yields a value, 
            // such as a method call.

            Log.Info(() => parameter, () => anIntegerWithAnUnusuallyLongName, () => localVar);

            Log.Info(() => localVar.Trim());
            Log.Info(() => localVar.Trim().Length);
            Log.Info(() => localVar.Trim().Length * 2);
            Log.Info(() => instance);
            Log.Info(() => instance.InstanceFieldMember);
            Log.Info(() => instance.InstancePropertyMember);
            Log.Info(() => TestClass.StaticFieldMember);
            Log.Info(() => TestClass.StaticPropertyMember);

            localVar = null;
            Log.Info(() => localVar);
            Log.Info(() => localVar.Length);
            Log.Info(() => null);
            Log.Info(() => 123);
            Log.Info(() => "literal");
        }

        // Recursive method for testing deeply nested calls.
        private static void Recurse(int i, int max)
        {
            using (Log.InfoCall("R " + i))
            {
                Log.Info("Depth = ", i);
                if (i == max) return;
                else Recurse(i + 1, max);
            }
        }
    }

    class Helper
    {
        // Declare a Logger instance for use by this class.
        private static readonly Logger Log = Logger.GetLogger("Helper");

        public static void Foo()
        {
            // Log.DebugCallThread() logs gives the current thread a name instead of a 
            // number in the viewer and logs entry/exit of the current method.
            using (Log.DebugCallThread("Helper.Foo thread"))
            {
                for (int i = 0; i < 100; ++i)
                {
                    Log.Debug(i, " squared is ", i * i);
               
                    if (i % 9 == 0)
                    {
                        Bar(i);
                        Thread.Sleep(1);
                    }
                }
            }
        }

        public static void Bar(int i)
        {
            using (Log.DebugCall())
            {
                Log.Verbose("Hello from Bar, i = ", i);
                Log.DebugFormat("i*i*i = {0}", i * i * i);
                Log.Debug("System tick count = ", Environment.TickCount);
            }
        }
    }

    // Class used to test/demo the logging of static and non-static 
    // fields and properties vie lambda expressions.
    class TestClass
    {
        public TestClass()
        {
            InstancePropertyMember = "Value of InstancePropertyMember";
            InstanceFieldMember = "Value of InstanceFieldMember";
        }

        static TestClass()
        {
            StaticPropertyMember = "Value of StaticPropertyMember";
            StaticFieldMember = "Value of StaticFieldMember";
        }

        public static string StaticFieldMember;
        public string InstanceFieldMember;

        public static string StaticPropertyMember
        {
            get;
            set;
        }

        public string InstancePropertyMember
        {
            get;
            set;
        }
    }

}

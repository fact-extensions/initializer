#if NET40
#define SUPPRESS_OFFICE
#endif

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Reflection;
//using System.Data.Common;
//using System.Data;
using System.IO;
using System.Xml.Serialization;
using System.Xml;
#if LEGACY_CONFIG_ENABLED
using System.Configuration;
#endif

using System.Collections;
using System.Threading;
using System.Diagnostics;
using System.Threading.Tasks;
#if FEATURE_POLICY
using Fact.Extensions.Configuration;
#endif
using Microsoft.Extensions.Logging;

namespace Fact.Extensions.Initialization
{
    /// <summary>
    /// Core loader and initializer for DLL modules.  Executed in a predictable way, starting with the most depended on DLLs and ending with the least depended on
    /// </summary>
    public interface ILoader
    {
        /// <summary>
        /// Dependent initialization for a particular assembly.  Happens in proper
        /// dependency order, so underlying DLL's are initialized first.
        /// </summary>
        void Initialize();
    }

    /// <summary>
    /// Normally Initialize operates under a kind of reverse hierarchy, so that core assemblies load and initialize, then parent assemblies, and so on.
    /// Usually this is desirable because assemblies have a way of depending on each other.  However, sometimes assemblies need
    /// to initialize before this chain - such as ORM handler registration.  Where normal Initialization occurs in a controlled, predictable manner
    /// the order of ILoaderAsync is undefined, with the important point that it does occur before the regular Initialize.
    /// 
    /// Because of this more free form nature, multiple ILoaderAsync instances may concurrently
    /// be Load()'ing at the same time.  Place as much as is safe into Load() to speed
    /// overall system initialization.
    /// </summary>
    public interface ILoaderAsync
    {
        void Load();
    }

    /// <summary>
    /// For loaders that need a shutdown phase too.  Optional
    /// </summary>
    public interface ILoaderShutdown
    {
        void Shutdown();
    }


	/// <summary>
	/// Warning: Not active
	/// </summary>
    public interface ILoaderReflector
    {
        void Evaluate(Type t, IEnumerable<object> attributes);
    }


    // temporarily disabling for NETCORE until Assembly/AppDomain API settles just a little bit, and
    // until my understanding of what's changed improves
    public static class Initializer
    {
        // TODO: make these readonly again asap
        static readonly ILogger logger = LogManager.GetLogger("Initialier");
        static readonly ILogger loggerGeneral = LogManager.GetLogger("General");
#if FEATURE_POLICY
        static readonly IPolicyProvider policyProvider = PolicyProvider.Get();
        static readonly ExceptionPolicy exceptionPolicy = policyProvider.GetExceptionPolicy();
#else
        static readonly ExceptionPolicy exceptionPolicy = new ExceptionPolicy();
#endif
        static bool _initialized = false;
        static bool _initializing = false;

        /// <summary>
        /// The initialize method has been invoked for the first time
        /// </summary>
        public static event Action InitializeCalled;
        /// <summary>
        /// Invokes after all the ILoaderAsync have run
        /// </summary>
        public static event Action AsyncComplete;
        /// <summary>
        /// Invokes after all the ILoader have run
        /// </summary>
        public static event Action SyncComplete;
        /// <summary>
        /// Invokes after all the ILoaderReflector have run
        /// </summary>
        public static event Action Reflected;
        /// <summary>
        /// Invokes when initialization is fully completed
        /// </summary>
        public static event Action Complete;
        /// <summary>
        /// Invoked when a particular assembly begins initialization
        /// </summary>
        public static event Action<Assembly> AssemblyInitBegin;
        /// <summary>
        /// Invoked when a particular assembly begins initialization
        /// </summary>
        public static event Action<Assembly> AssemblyInitComplete;
        /// <summary>
        /// Invoked when a particular assembly completes async loading
        /// </summary>
        public static event Action<Assembly> AssemblyAsyncComplete;
        /// <summary>
        /// Invoked when a particular assembly begins async loading
        /// </summary>
        public static event Action<Assembly> AssemblyAsyncBegin;
        /// <summary>
        /// Beginning assembly dig/inspection process
        /// </summary>
        public static event Action Digging;

        static object locker = new object();
        static object stateChangeLocker = new object();

        /// <summary>
        /// MutEx lock handle for interacting with Initializer.  Locks on entire initialization phase
        /// only allowing locks before or after initiatization
        /// </summary>
        static public object Locker { get { return locker; } }

        /// <summary>
        /// MutEx lock handle for interacting with Initializer.  Locks only on state changes 
        /// (IsAsyncInitialized flag changing, etc)
        /// </summary>
        static public object StateChangeLocker { get { return stateChangeLocker; } }


        /// <summary>
        /// For OnAsyncComplete/OnInitComplete methods to ensure they don't
        /// fire a synchronous handler while asynchronous ones are running
        /// (since order tends to be important, we want the already running
        /// i.e. previously registered ones to go first)
        /// </summary>
        /// <remarks>TODO: consider replacing this mechanism with a classic semaphore</remarks>
        static object eventFireLocker = new object();

        /// <summary>
        /// Either
        /// 
        /// 1) When Complete phase runs, run the specified action OR
        /// 2) If Complete phase already has run, run the specified action immediately
        /// </summary>
        /// <param name="action"></param>
        public static void OnInitialized(Action<bool> action)
        {
            lock (StateChangeLocker)
            {
                if (!IsInitialized)
                {
                    Complete += () => action(true);
                }
                else
                {
                    logger.LogDebug("OnInitialized eventFireLocker start");

                    lock (eventFireLocker)
                        action(false);

                    logger.LogDebug("OnInitialized eventFireLocker end");
                }
            }
        }


        /// <summary>
        /// Wait for async phase of initialization to completely finish - presumably
        /// on a separate thread than this one.  If Initialization is
        /// already complete, this returns immediately
        /// </summary>
        public static void WaitAsyncComplete()
        {
            var evh = new EventWaitHandle(false, EventResetMode.AutoReset);
            lock (StateChangeLocker)
            {
                if (!IsAsyncInitialized)
                {
                    AsyncComplete += () => evh.Set();
                }
                else
                    return;
            }
            evh.WaitOne();
        }


        /// <summary>
        /// Wait for initialization to completely finish - presumably
        /// on a separate thread than this one.  If Initialization is
        /// already complete, this returns immediately
        /// </summary>
        public static void WaitComplete()
        {
            var evh = new EventWaitHandle(false, EventResetMode.AutoReset);
            lock (StateChangeLocker)
            {
#if DEBUG
                logger.LogDebug("Initializer::WaitComplete phase 0 IsInitialized = " + IsInitialized);
#endif

                if (!IsInitialized)
                {
#if DEBUG
                    Complete += () => logger.LogDebug("Initializer::WaitComplete completed");
#endif
                    Complete += () => evh.Set();
                }
                else
                    return;

#if DEBUG
                logger.LogDebug("Initializer::WaitComplete phase 1 IsInitialized = " + IsInitialized);
#endif
            }
            evh.WaitOne();
        }

        /// <summary>
        /// Either:
        /// 
        /// 1) When LoadAsync phase has completed run the specified action, or
        /// 2) If LoadAsync phase has already completed, run the specified action immediately
        /// </summary>
        /// <param name="action">parameter is TRUE when action is deferred</param>
        public static void OnAsyncComplete(Action<bool> action)
        {
            logger.LogDebug("Initializer::OnAsyncComplete StateChangeLocker start");

            // Ensure status checks do not change
            lock (StateChangeLocker)
            {
                // If async loads have not completed
                if (!IsAsyncInitialized)
                    // tag on to the completion event
                    AsyncComplete += () => action(true);
                else
                {
                    logger.LogDebug("Initializer::OnAsyncComplete eventFireLocker start");

                    lock (eventFireLocker)
                        // run immediately
                        action(false);

                    logger.LogDebug("Initializer::OnAsyncComplete eventFireLocker end");
                }
            }

            logger.LogDebug("Initializer::OnAsyncComplete StateChangeLocker end");
        }

        /// <summary>
        /// Extended version of assembly.GetTypes() - presumably if there are no types, default behavior is to throw an exception
        /// This method will merely return an empty array
        /// Not pretty, but effective
        /// </summary>
        /// <param name="assembly"></param>
        /// <returns></returns>
		/// <remarks>FIX: This was used but is no longer.  Keep an eye on it</remarks>
        public static Type[] GetTypesOrDefault(this Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch
            {
                return new Type[0];
            }
        }

#if !APR_193
        static List<Assembly> APR_193_PluginAssemblies = new List<Assembly>();
#endif
        /// <summary>
        /// Recursive loader/initializer
        /// </summary>
        static AssemblyDependencyBuilder db;

        /// <summary>
        /// Initialize all subsystems
        /// </summary>
        /// <param name="executing">Which assembly is the top-level to evaluate down from</param>
        /// <remarks>
        /// WCF and ASP.NET report null for GetEntryAssembly()
        /// 
        /// With ASP.NET it's sensible to execute a GetExecutingAssembly() and pass it in
        /// With WCF we need to provide a hint in our App.config file
        /// </remarks>
        public static void Initialize(Assembly executing, bool waitToComplete = true)
        {
            lock (locker)
            {
                if (!_initialized && !_initializing)
                {
                    if (InitializeCalled != null)
                        InitializeCalled();

                    _initializing = true;

                    try
                    {
                        logger.LogInformation("Initializer::Initialize: using '" + executing.FullName + "' as entry assembly");
                        logger.LogInformation("Initializer::Initialize: executing = " + executing);

                        var assemblyListTask = Task.Factory.StartNew(() =>
                        {
							IEnumerable<Assembly> assemblyList = Enumerable.Empty<Assembly>();

#if !NETCORE
                            Thread.CurrentThread.Name = "Async Initializer";
#endif

#if MONODROID || NETCORE
								var manualLoad = ""; // FIX: for now, brute force manual load to nothing.  In MonoDroid,
								// dynamic plugin loading may be a no-no anyway
#else
                            // this is slightly different than entry.assemly: once entry assembly is known, sometimes
                            // dependent assemblies still aren't discovered (often the case with IoC-only/plugin DLL's).  Use
                            // this to nudge the initializer into knowing what to do.  Format is:
                            //
                            // fact.apprentice.core.initializeAssembly = [shortname],[shortname],[shortname] 
							var manualLoad = System.Configuration.ConfigurationManager.AppSettings["fact.apprentice.core.initializeAssembly"];
#endif

                            // the initial appSetting load can be time consuming.  Log here to see just how long
                            // it takes
                            loggerGeneral.LogDebug("Initialize: manualLoad = " + manualLoad);

                            if (!string.IsNullOrEmpty(manualLoad))
                            {
                                loggerGeneral.LogInformation("Initialize: Untested portion.  Forcing ToArray() to fail fast");
                                var manualLoadSplit = manualLoad.Split(',');
                                
                                var _assemblyList = manualLoadSplit.Select(x => Assembly.Load(new AssemblyName(x)));
                                assemblyList = _assemblyList.ToArray();
                            }

#if EXPERIMENTAL_PLUGIN_SUPPORT                            
                            AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;
#endif

#if PLUGIN_SUPPORT

							var manualLoadDir = Global.Config.PluginDirectory;

                            if (manualLoadDir != null)
                            {
                                manualLoadDir = Utility.MapVirtualPath(manualLoadDir);
 
                                var dirInfo = new DirectoryInfo(manualLoadDir);
                                try
                                {
                                    var result = from n in dirInfo.GetFiles("*.dll")
                                                 let asm = Assembly.LoadFrom(n.FullName)
                                                 select asm;

#if !APR_193
                                    APR_193_PluginAssemblies.AddRange(result);
                                    assemblyList = assemblyList.Concat(APR_193_PluginAssemblies);
#else
                                assemblyList = assemblyList.Concat(result);
#endif
                                }
                                catch (DirectoryNotFoundException e)
                                {
                                    loggerGeneral.LogError("Initialize plugin initialization failed: cannot open directory " + manualLoadDir + " / " + e.Message);
                                    loggerGeneral.LogError("Initialize plugin initialization failed: stack trace" + e.StackTrace, e);
                                }
                            }
#endif

                            return assemblyList;
                        });

                        if (Digging != null)
                            Digging();

                        db = new AssemblyDependencyBuilder(assemblyListTask);

                        db.ItemCreated += _item =>
                        {
                            var item = (AssemblyDependencyBuilder._Item) _item;

                            item.InitBegin += __item =>
                            {
                                if (AssemblyInitBegin != null)
                                    AssemblyInitBegin(item.value);
                            };

                            item.Initialized += __item =>
                            {
                                if (AssemblyInitComplete != null)
                                    AssemblyInitComplete(item.value);

                                loaded.AddLast(__item.value);
                            };

                            item.AsyncBegin += __item =>
                            {
                                if (AssemblyAsyncBegin != null)
                                    AssemblyAsyncBegin(item.value);
                            };

                            item.AsyncEnd += __item =>
                            {
                                if (AssemblyAsyncComplete != null)
                                    AssemblyAsyncComplete(item.value);
                            };
                        };

                        db.Completed += SetInitialized;
                        db.AsyncCompleted += () =>
                        {
                            lock (stateChangeLocker)
                            {
                                IsAsyncInitializing = false;
                                IsAsyncInitialized = true;
                            }

                            logger.LogDebug("Initializer::Initialize eventFireLocker start");
                            lock (eventFireLocker)
                            {
                                if (AsyncComplete != null)
                                    AsyncComplete();
                            }
                            logger.LogDebug("Initializer::Initialize eventFireLocker end");
                        };

                        lock (stateChangeLocker)
                        {
                            IsAsyncInitializing = true;
                        }

                        db.Dig(executing, null);
                        if (waitToComplete)
                            WaitComplete();
                        return;

                        // Loader reflection is not used yet, keep the code here if we wish to use
                        // it again at some point
                        // FIX: ILoaderReflection totally broken, but shouldn't be a hugely difficult fix.  Also not in use anywhere-
#if UNUSED

                        var loaderReflection = (from n in ToLoad
                                               let loaderReflector = n.loader as ILoaderReflector
                                               where loaderReflector != null
                                               select loaderReflector).ToArray();

                        // only start loader reflection process if any loader reflectors are even
                        // around to execute
                        if (loaderReflection.Length > 0)
                        {
                            ThreadPool.QueueUserWorkItem(x =>
                            {
                                Logger.Write("Initializer::Initialize - starting loader reflection process");

                                try
                                {
                                    var allTypes = (from n in ToLoad.Select(y => y.type.Assembly)   // grab all assemblies which have an ILoader
                                                    from i in n.GetTypesOrDefault()                 // per each of those assemblies, get a list of all types within the assembly
                                                    // per each type in the assembly, get custom attribute which isn't a system attribute
                                                    let ca = i.GetCustomAttributes(false).Where(y => !y.GetType().FullName.StartsWith("System."))
                                                    where ca != null && ca.Count() > 0
                                                    // flatten to a long list of potentially relevant Types & custom attributes
                                                    select new { i, ca }).ToArray();

                                    // iterate through all potentially interesting Types  & custom attributes
                                    foreach (var item in allTypes)
                                    {
                                        // give each ILoaderReflection a crack at it
                                        foreach (var loader in loaderReflection)
                                        {
                                            loader.Evaluate(item.i, item.ca);
                                        }
                                    }
                                }
                                catch (Exception e)
                                {
                                    Logger.Write("Initializer::Initialize: loader reflector failed: " + e.Message, "General", 0, 0, TraceEventType.Error);
                                    throw;
                                }
                                finally
                                {

                                    Logger.Write("Initializer::Initialize - initialization complete");

                                    //ToLoad = null;
                                    if (Reflected != null)
                                        Reflected();

                                    lock (StateChangeLocker)
                                    {
                                        IsInitialized = true;
                                        _initializing = false;
                                        IsInitializedEventFiring = true;
                                    }

                                    lock(eventFireLocker)
                                    { 
                                        if (Complete != null)
                                            Complete();
                                    }

                                    lock (StateChangeLocker)
                                        IsInitializedEventFiring = false;
                                }
                            });
                        }
                        else
                        {
                            Logger.Write("Initializer::Initialize - initialization complete");

                            lock (StateChangeLocker)
                            {
                                IsInitialized = true;
                                _initializing = false;
                                IsInitializedEventFiring = true;
                            }

                            //ToLoad = null;
                            lock (eventFireLocker)
                            {
                                if (Complete != null)
                                    Complete();
                            }

                            lock (StateChangeLocker)
                                IsInitializedEventFiring = false;
                        }
#endif
                    }
                    catch (Exception e)
                    {
                        exceptionPolicy.HandleException(e);
                        /*
                        // Debug: can check currentlyLoadItem here
                        loggerGeneral.LogErrorWithInspection("Problem during initialization: ", e);
                        throw;*/
                    }
                }
                else
                {
                    logger.LogInformation("Initializer::Initialize: Already initialized");
                }
            }
        }


        static void SetInitialized()
        {
            lock (StateChangeLocker)
            {
                IsInitialized = true;
                _initializing = false;
                IsInitializedEventFiring = true;
            }

            logger.LogDebug("Initializer::SetInitialized eventFireLocker start");
            lock (eventFireLocker)
            {
                if (Complete != null)
                    Complete();
            }
            logger.LogDebug("Initializer::SetInitialized eventFireLocker end");

            lock (StateChangeLocker)
                IsInitializedEventFiring = false;
        }

#if EXPERIMENTAL_PLUGIN_SUPPORT
        /// <summary>
        /// Tries to resolve assemblies directly out of our specified plugin directory
        /// 
        /// Beware, this could be a performance penalty alongside CompositeFactory's version (CompositeFactory may use
        /// this regularly).  SHOULD be OK, but keep an eye on this
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="args"></param>
        /// <returns></returns>
        /// <remarks>Turns out this isn't really used, the APR_193 loading happens already, and the system
        /// then knows where to find assemblies thus not probing this event.  
        /// Keeping it around just incase the code is useful later</remarks>
        static Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
        {
            var name = args.Name;
            var assembly = APR_193_PluginAssemblies.SingleOrDefault(x => 
                {
                    var _name = x.GetName();
                    return name == _name.FullName;
                });

            return assembly;
        }
#endif

        /// <summary>
        /// Returns true when all subsytems have completed initialization
        /// </summary>
        public static bool IsInitialized 
        { 
            get {  return _initialized; }
            set
            {
                lock (stateChangeLocker)
                {
                    _initialized = value;
                }
            }
        }

        public static bool IsInitializedEventFiring { get; private set; }

        /// <summary>
        /// Returns true when all subsystems have completed their ILoaderAsync phase
        /// </summary>
        public static bool IsAsyncInitialized { get; private set; }

        public static bool IsAsyncInitializedEventFiring { get; private set; }

        /// <summary>
        /// Returns true when subsystems are currently running their ILoaderAsync phase.  Otherwise, false
        /// </summary>
        public static bool IsAsyncInitializing { get; private set; }

#if FEATURE_VERSION_INSPECT
        /// <summary>
        /// Acquires Apprentice-specific version info from an assembly
        /// </summary>
        /// <param name="assembly"></param>
        /// <remarks>This will be broken since new builds don't autogenerate version stamps</remarks>
        /// <returns></returns>
        static string GetVersion(Assembly assembly)
        {
            var type = assembly.GetType("Fact._Global.VersionInfo");
            if (type == null)
                return null;

            var field = type.
                GetTypeInfo().
                GetField("Version");

            var value = (string) field.GetValue(null);

            return value;
        }

        /// <summary>
        /// This will be broken since version-stamp-embedding hasn't been brought back
        /// </summary>
        /// <returns></returns>
        public static IEnumerable<KeyValuePair<Assembly, string>> Versions
        {
            get
            {
                /*
                return from n in ToLoad
                       let asm = n.type.Assembly
                       let version = GetVersion(asm)
                       where version != null
                       select new KeyValuePair<Assembly, string>(asm, version);*/

                return loaded.Select(x => new KeyValuePair<Assembly, string>(x, GetVersion(x)));
            }
        }
#endif

        static LinkedList<Assembly> loaded = new LinkedList<Assembly>();

        /// <summary>
        /// Temporary list of already-inspected assemblies, to avoid recursing in further than we need to
        /// </summary>
        internal static HashSet<string> Inspected = new HashSet<string>();

        /// <summary>
        /// Prefix to look for to not recurse further while digging
        /// </summary>
        public static LinkedList<string> SkipAssembly = new LinkedList<string>();

        static Initializer()
        {
            SkipAssembly.AddLast("Microsoft.");
            //SkipAssembly.AddLast("mscorlib.");
            SkipAssembly.AddLast("System.");
            SkipAssembly.AddLast("Castle.");
            SkipAssembly.AddLast("Newtonsoft.");
            SkipAssembly.AddLast("Telerik.");

#if MONO
            SkipAssembly.AddLast("Mono.");
#endif

            Inspected.Add("System");
            Inspected.Add("mscorlib");

#if MONO
            Inspected.Add("glib-sharp");
            Inspected.Add("gdk-sharp");
            Inspected.Add("gtk-sharp");
#else
            Inspected.Add("PresentationFramework");
            Inspected.Add("PresentationCore");
#endif
        }

        // Not deleting yet, because I haven't yet put in the APR-193 specialized code, so keeping this
        // here for code reference
#if UNUSED
        /// <summary>
        /// Inspect and add assemblies to the initialization list.  Remember, if any assembly does NOT implement an ILoader, then its dependent assemblies will
        /// not be evaluated.  The exception is the very top level (0) does not need an ILoader
        /// </summary>
        /// <param name="master"></param>
        /// <param name="manuallySpecified"></param>
        /// <param name="level">How far down we've descended into recursion</param>
        static void Dig(Assembly master, IEnumerable<Assembly> manuallySpecified, int level)
        {
            //var _currentLevel = currentLevel;

            Type type;

            try
            {
                type = master == null ? null : master.GetType("Fact._Global.Loader");
            }
            catch
            {
                throw;
            }

            // we let the very first (executing assembly) get away with not having an ILoader.  Simple & test
            // scenarios often don't call for one
            if (type == null && level > 0) return;

            Logger.Write("Initializer::Dig: assembly = " + master);

            IEnumerable<Assembly> referenced;

            if (master != null)
            {
                referenced = master.GetReferencedAssemblies().Select(x => 
                    {
                        try
                        {
                            return Assembly.Load(x);
                        }
                        catch (Exception e)
                        {
                            Logger.Write("Initializer::Dig: failed to load assembly: " + x.Name, "General", 0, 0, TraceEventType.Error);
                            Logger.Write("Initializer::Dig: exception: " + e.Message, "General", 0, 0, TraceEventType.Error);
                            throw;
                        }
                    });
                referenced = referenced.Concat(manuallySpecified);
            }
            else
            {
                referenced = manuallySpecified;
            }

            foreach (var name in referenced)
            {
                var _name = name.GetName().Name;

                // if we've not inspected this assembly yet, and it's not part of our skip assembly list, then dig into it
                if (!Inspected.Contains(_name) && SkipAssembly.FirstOrDefault(x => _name.StartsWith(x)) == null)
                {
                    Logger.Write("Initializer::Dig: inspecting = " + name, "General", 0, 0, System.Diagnostics.TraceEventType.Verbose);

                    // add to our growing ignore list, so as to not get into recursive loops
                    Inspected.Add(_name);

                    // dig into this particular assemly, which will at a minimum add this assembly's ILoader to the ToLoad list, if it has one
                    try
                    {
                        //_currentLevel = currentLevel;
                        Dig(name, Enumerable.Empty<Assembly>(), level + 1);
                        //currentLevel = _currentLevel;
                    }
                    catch(Exception e)
                    {
                        throw new Exception("Initialization exception during dig of: " + name, e);
                    }
                }
            }

            // putting this out here ensures that we get all the way to the "bottom" of the recursion
            // before adding anything
            if (type != null)
            {
#if !APR_193
                if (APR_193_PluginAssemblies.Contains(type.Assembly))
                {
                    ToLoad.AddLast(new ToLoadItem(type, null));
                }
                else
#endif
                {
                    loaderInitializePending++;
                    ToLoad.AddLast(new ToLoadItem(type, decrementLoaderInitializePending));
                }
            }
        }
#endif

        /// <summary>
        /// Useful because under certain circumstances (such as webserver environment) running on a different thread produces different results 
        /// </summary>
        /// <returns></returns>
        public static Assembly GetEntryAssembly()
        {
            var entry = Assembly.GetEntryAssembly();

            if (entry == null)
            {
                // WCF & unit test hosted scenarios don't tell us what the entry assembly is
                // so we need to give ourselves a hint in to app.config file
#if MONODROID
				// FIX: Pull this out of SharedPreferences for Mono
				string str = null;
#else
                var str = ConfigurationManager.AppSettings["assembly.entry"];
#endif

                if (!string.IsNullOrEmpty(str))
                {
                    logger.LogInformation("Initialize::Initialize: attempting to use '" + str + "' as entry assembly");

                    entry = Assembly.Load(str);
                }
                else
                {
                    // if we can't get entry assembly, and there's no configuration specification either, assume
                    // that the calling assembly is the right one and start from there
                    entry = Assembly.GetCallingAssembly();
                }
            }

            return entry;
        }

        /// <summary>
        /// Kick off initialization subsystem.  Keep in mind the assembly chain will start searching from one of 3
        /// places, in the following order:
        /// 
        /// 1. entry assembly.  In WCF and unit test scenarios this returns null, so ...
        /// 2. gather name from app settings "assembly.entry".  If this is null, then..
        /// 3. assembly which is invoking this Initialize() method
        /// </summary>
        public static void Initialize()
        {
            if (!_initialized)
            {
                // A little kludgey.  GetEntryAssembly sometimes looks
                // one up the call stack to determine who the 'actual'
                // initializing assembly/exe is.  This works badly
                // when Initialize() is called directly, since one up
                // the stack is still Fact.Apprentice.Core*.  Therefore
                // we treat Fact.Apprentice.Core as a 'null' result and
                // call GetCallingAssembly here to retrieve TRUE
                // caller.
                //
                // * If code moves between different assemblies,
                // this comparison may break, so this is a little 
                // bit kludgey
                var coreAssembly = Assembly.GetExecutingAssembly();
                var entryAssembly = GetEntryAssembly();

                if(entryAssembly == coreAssembly)
                    entryAssembly = Assembly.GetCallingAssembly();

                Initialize(entryAssembly);
            }
        }



        /// <summary>
        /// Beware this may have trouble acquiring top level assembly
		/// Note also a Task.Wait() from here will only indicate the dig has finished,
		/// not the full initialization has completed.  Use WaitComplete() call for actual completion
        /// </summary>
        /// <returns></returns>
        public static Task InitializeAsync()
        {
			var coreAssembly = Assembly.GetExecutingAssembly();
			var entryAssembly = GetEntryAssembly();

			if(entryAssembly == coreAssembly)
				entryAssembly = Assembly.GetCallingAssembly();

			return Task.Factory.StartNew(() => Initialize(entryAssembly, false));
        }


        /// <summary>
        /// Kick off shutdown logic for any and all Loaders with the ILoaderShutdown interface
        /// </summary>
		public static void Shutdown()
        {
			WaitComplete ();
            
			db.Shutdown();
        }


        /// <summary>
        /// Does just what one would expect, initiates Initializer.Shutdown call on its own thread, 
        /// and returns the associated Task object so that a .Wait() call may be performed
        /// </summary>
        /// <returns></returns>
        public static Task ShutdownAsync()
        {
            return Task.Factory.StartNew(Shutdown);
        }
    }

	public class AssemblyDependencyBuilder : 
		InitializingDependencyBuilder<Assembly>
	{
        static readonly ILogger logger = LogManager.CreateLogger<AssemblyDependencyBuilder>();
        static readonly ILogger loggerInit = LogManager.GetLogger("Initialization");
#if FEATURE_POLICY
        static readonly IPolicyProvider policyProvider = PolicyProvider.Get();
        static readonly ExceptionPolicy exceptionPolicy = policyProvider.GetExceptionPolicy();
#else
        static readonly ExceptionPolicy exceptionPolicy = new ExceptionPolicy();
#endif

        Task<IEnumerable<Assembly>> assemblyList;

		public AssemblyDependencyBuilder(Task<IEnumerable<Assembly>> assemblyList)
		{
			this.assemblyList = assemblyList;
		}


		public bool IsAsyncInitialized { get; set; }

		/// <summary>
		/// Participate during _Item construction to initialize first (root) item, if necessary
		/// </summary>
		/// <param name="obj"></param>
		void InitializeRoot(_Item obj)
		{
			// First node that is created is the root node
			if (rootNode == null)
			{
				rootNode = (_Item)obj;
				// FIX: outside events latch on to initialized *after* this InitializeRoot is called, meaning other folks will be notified
				// of assembly init AFTER global Complete event fires via rootNode.  
                // Not a killer, but incorrect and needs fixing
				rootNode.Initialized += item =>
				{
                    //Logger.Write("InitializeRoot completed");

					if (Completed != null)
						Completed();
				};
				rootNode.DigEnded += item =>
				{
					Dig(item, item.value, assemblyList.Result);
					/*				
	                    // iterate through all assemblies passed in during creation of DependencyBuilder object
					foreach (var child in assemblyList.Result)
					{
						// force add & dig through all these children
						item.AddChild(GetValue(child));
						Dig(child, null);
					}*/

					// this is to indicate that the rootNode specifically has been dug, and to wake up any waiting
					// initializers - since we have to wait for all digs to finish before we can actually begin
					// initialization (because digs are what kick off IAsyncLoaders)
					rootNodeEvh.Set();
				};
			}
		}

		_Item rootNode;

		/// <summary>
		/// Set when dug has completed
		/// </summary>
		EventWaitHandle rootNodeEvh = new EventWaitHandle(false, EventResetMode.ManualReset);

		protected override DependencyBuilder<Assembly>.Item CreateItem(Assembly key)
		{
			return new _Item(key, this);
		}

		HashSet<_Item> AsyncInitializing = new HashSet<_Item>();

		/// <summary>
		/// First in last out buffer for shutdown
		/// </summary>
		LinkedList<_Item> FILO = new LinkedList<_Item>();

		/// <summary>
		/// 
		/// </summary>
		/// <remarks>
		/// TODO: Someday make this partial-async also
		/// (the same way post ILoaderAsync is async, not ILoaderAsync style itself) 
		/// Looks like we could do a Dig again, but this time fire things off immediately on create instead of
		/// on dig event.  For now roll with this old-style shutdown
		/// </remarks>
		public void Shutdown()
		{
            foreach (var item in FILO)
            {
                loggerInit.LogInformation("Shutdown for assembly: " + 
                    item.loader.GetType().GetTypeInfo().Assembly.FullName);
                ((ILoaderShutdown)item.loader).Shutdown();
            }
		}

		/// <summary>
		/// Fired when total initialization has completed
		/// </summary>
		public event Action Completed;

		/// <summary>
		/// Fired after all ILoaderAsync have completed.  May be a small delay since it is fired
		/// by initialization process and not ILoaderAsync servicer itself
		/// </summary>
		public event Action AsyncCompleted;

		public class _Item : Item
		{
			AssemblyDependencyBuilder parent;
			internal readonly object loader;

			/// <summary>
			/// This item/assembly only should be dug into if it is either a rootnode OR it has loader code
			/// </summary>
			public override bool ShouldDig
			{
				get
				{
					if (parent.rootNode == this)
						return true;

					if (loader is ILoader || loader is ILoaderAsync)
						return true;

					return false;
				}
			}

			protected override bool ShouldInitialize
			{
				get
				{
					var hasLoader = loader is ILoader;

					loggerInit.LogInformation("Inspecting: " + Name + (hasLoader ? " (has loader)" : ""));

                    return hasLoader;
#if UNUSED
					if (!hasLoader)
					{
						// It's possible this is the root node and if we don't issue an initialization,
						// then the standard Completed event won't get fired, so be sure to do it here
                        // FIX: Define "we don't issue an initialization" I have forgotten what this means
						if (this == parent.rootNode)
						{
                        // FIX: it seems possible this block is entered inappropriately, it seems
                        // it shouldn't be entered if Initialize() got called.

                        // if Initilialize() got called, this wait call will immediately return
							WaitForAsyncInitializers();

                        // FIX: Stopgap, async completed gets fired off twice, once here
                        // and once in main Initialize() call 
                            if (!parent.IsAsyncInitialized)
                            {
                                parent.IsAsyncInitialized = true;

                                if (parent.AsyncCompleted != null)
                                    parent.AsyncCompleted();
                            }

							// since there's no actual initialization phase in this case, fire off completed right away

                        // FIX: This is firing parent.Completed twice.  Here is actually the first time,
                        // and elsewhere after EvalInitialize gets called a 2nd time, during MONO testing.  
                        // Even though it doesn't have a loader, EvalInitialize is picking it up (rootNode)
                        // this is a bug.
							// TODO: Need to fire off AsyncCompleted here also--
							if (parent.Completed != null)
								parent.Completed();
						}

						return false;
					}
					return true;
#endif
				}
			}


			/// <summary>
			/// Wait for IAsyncLoaders to fully complete
			/// </summary>
			void WaitForAsyncInitializers()
			{
				// this code results in ThreadPool being a queue of sorts as the task
				// waits.  Not a fantastic choice, but not so bad either
            // we do while comparison within lock statement  
                for(;;)
				{
					Task task;

					lock (parent.AsyncInitializing)
					{
                        // equivelant of a while loop, but thread safe
                        if (parent.AsyncInitializing.Count == 0)
                            break;

						// look for something a little faster than "First()" call
						var _item = parent.AsyncInitializing.First();
						task = _item.loaderAsyncTask;
					}
					task.Wait();
				}
			}

            protected override void WaitForDependencies()
            {
                // We have to wait until root Node has been "dug" completely, otherwise
                // not all async loaders may have had a chance to start
                parent.rootNodeEvh.WaitOne();

                // Wait for "phase 1" async initializers to complete.  Waiting at this point makes sense, because a 
                // full "dig" as we waited for above means all async initializers have kicked off
                WaitForAsyncInitializers();

                // Only fire overall AsyncCompleted event once, the first time we come in to the initialization phase
                if (!parent.IsAsyncInitialized)
                {
                    parent.IsAsyncInitialized = true;

                    if (parent.AsyncCompleted != null)
                        parent.AsyncCompleted();
                }
            }

			protected override void Initialize()
			{
#if UNUSED
				// We have to wait until root Node has been "dug" completely, otherwise
				// not all async loaders may have had a chance to start
				parent.rootNodeEvh.WaitOne();

				// Wait for "phase 1" async initializers to complete.  Waiting at this point makes sense, because a 
				// full "dig" as we waited for above means all async initializers have kicked off
				WaitForAsyncInitializers();

				// Only fire overall AsyncCompleted event once, the first time we come in to the initialization phase
				if (!parent.IsAsyncInitialized)
				{
					parent.IsAsyncInitialized = true;

					if (parent.AsyncCompleted != null)
						parent.AsyncCompleted();
				}
#endif
				if (InitBegin != null)
					InitBegin(this);

				loggerInit.LogInformation("Initializing: " + Name + ": " + Children.Cast<Item>().
					Select(x => x.Name + (x.IsInitialized ? " " : " not ") + "initialized").
					ToString(", "));

                try
                {
                    ((ILoader)loader).Initialize();
                }
                catch(TypeLoadException tle)
                {
                    loggerInit.LogError("Initializing: " + Name + " failed.  Could not load assembly because " + tle.TypeName + " could not be loaded");
                    throw new TypeLoadException("Could not initialize Assembly " + Name + " because " 
                        + tle.TypeName + " could not be loaded", tle); 
                }
                catch(Exception e)
                {
                    // TODO: bring this back, useful to have the deep stack tracing
                    //loggerInit.ErrorWithInspection("Initializing: " + Name + " failed.", e);
                    loggerInit.LogError("Initializing: " + Name + " failed.", e);
                    throw new InvalidOperationException("Could not initialize: " + Name, e);
                }

				loggerInit.LogInformation("Initialized: " + Name);
			}

			public _Item(Assembly key, AssemblyDependencyBuilder parent) 
			{
				this.parent = parent;
				loader = key.CreateInstance("Fact._Global.Loader");
				parent.InitializeRoot(this);
				/*			
	                // If this isn't an init/shutdown participating assembly, add it to an exclude cache so that next
				// time we startup we don't bother with it
				if (!(loader is ILoader || loader is ILoaderAsync || loader is ILoaderShutdown))
				{

				}*/
				if(loader is ILoaderShutdown)
				{ 
					Initialized += item =>
					{
						lock (parent.FILO)
						{
							parent.FILO.AddFirst(this);
						}
					};
				}
			}

			/// <summary>
			/// Have this so that we have a chance to latch on some event listeners
			/// </summary>
			public override void Start()
			{
				var loaderAsync = loader as ILoaderAsync;

				if (loaderAsync != null)
				{
					loggerInit.LogInformation("Initializing (async kickoff): " + this.value.GetName().Name);

					lock (parent.AsyncInitializing)
					{
						parent.AsyncInitializing.Add(this);
					}

					if (AsyncBegin != null)
						AsyncBegin(this);

#if NET40
                    loaderAsyncTask = taskFactory.StartNew(() =>
                    {
                        loggerInit.LogInformation("Initializing (async actual): " + this.value.GetName().Name);

                        loaderAsync.Load();

                        loggerInit.LogInformation("Initialized (async): " + this.value.GetName().Name);
                    }, exceptionPolicy.HandleException);

                    loaderAsyncTask.ContinueWith(antecendent =>
                    {
                        lock (parent.AsyncInitializing)
                        {
                            parent.AsyncInitializing.Remove(this);
                        }

                        if (AsyncEnd != null)
                            AsyncEnd(this);
                    });
#else
                    loaderAsyncTask = taskFactory.StartNew(() =>
                        {
                            loggerInit.LogInformation("Initializing (async actual): " + this.value.GetName().Name);

                            loaderAsync.Load();
                            lock (parent.AsyncInitializing)
                            {
                                parent.AsyncInitializing.Remove(this);
                            }

                            if (AsyncEnd != null)
                                AsyncEnd(this);

                            loggerInit.LogInformation("Initialized (async): " + this.value.GetName().Name);
                        }, exceptionPolicy.HandleException);
#endif
				}
			}

			/// <summary>
			/// Called when ILoader initialize portion begins
			/// </summary>
			/// <remarks>
			/// These differenciate from "Initialized" event because this InitBegin specifically fires *AFTER* AsyncEnd, and
			/// also this InitEnd fires *BEFORE* Completed
			/// </remarks>
			public event Action<_Item> InitBegin;

			/// <summary>
			/// Called when ILoaderAsync portion begins
			/// </summary>
			public event Action<_Item> AsyncBegin;
			/// <summary>
			/// Called when ILoaderAsync portion is complete
			/// </summary>
			public event Action<_Item> AsyncEnd;

			Task loaderAsyncTask;

			public override string Name { get { return value.GetName().Name; } }

			public override IEnumerable<Assembly> GetChildren()
			{
				return value.GetReferencedAssemblies().
					Where(x => Initializer.SkipAssembly.FirstOrDefault(y => x.Name.StartsWith(y)) == null).
					Where(x => !Initializer.Inspected.Contains(x.Name)).
					Select(LoadAssembly);
			}

            Assembly LoadAssembly(AssemblyName name)
            {
                try
                {
                    return Assembly.Load(name);
                }
                catch(Exception e)
                {
                    throw new Exception("Failure while loading dependencies for: " + Name, e);
                }
            }
		}

		protected override object GetKey(Assembly key)
		{
			return key.FullName;
		}
	}
}
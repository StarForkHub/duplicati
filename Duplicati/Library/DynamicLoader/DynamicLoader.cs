// Copyright (C) 2024, The Duplicati Team
// https://duplicati.com, hello@duplicati.com
// 
// Permission is hereby granted, free of charge, to any person obtaining a 
// copy of this software and associated documentation files (the "Software"), 
// to deal in the Software without restriction, including without limitation 
// the rights to use, copy, modify, merge, publish, distribute, sublicense, 
// and/or sell copies of the Software, and to permit persons to whom the 
// Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in 
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS 
// OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, 
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE 
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER 
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING 
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER 
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace Duplicati.Library.DynamicLoader
{
    /// <summary>
    /// This class supports dynamic loading of instances of a given interface
    /// </summary>
    /// <typeparam name="T">The interface that the class loads</typeparam>
    internal abstract class DynamicLoader<T> where T : class
    {
        /// <summary>
        /// The tag used for logging
        /// </summary>
        private static readonly string LOGTAG = Logging.Log.LogTagFromType<DynamicLoader<T>>();

        /// <summary>
        /// A lock used to guarantee threadsafe access to the interface lookup table
        /// </summary>
        protected readonly object m_lock = new object();

        /// <summary>
        /// A cached list of interfaces
        /// </summary>
        protected Dictionary<string, T> m_interfaces;

        /// <summary>
        /// Function to extract the key value from the interface
        /// </summary>
        /// <param name="item">The interface to extract the key from</param>
        /// <returns>The key for the interface</returns>
        protected abstract string GetInterfaceKey(T item);

        /// <summary>
        /// Gets a list of subfolders to search for interfaces
        /// </summary>
        protected abstract string[] Subfolders { get; }

        /// <summary>
        /// Construct a new instance of the dynamic loader,
        ///  does not load anything
        /// </summary>
        protected DynamicLoader()
        {
        }

        /// <summary>
        /// Provides threadsafe doublelocking loading of the interface table
        /// </summary>
        protected void LoadInterfaces()
        {
            if (m_interfaces == null)
                lock (m_lock)
                    if (m_interfaces == null)
                    {
                        Dictionary<string, T> interfaces = new Dictionary<string, T>();
                        //When loading, the subfolder matches are places last in the
                        // resulting list, and thus applied last to the lookup,
                        // meaning that they can replace the stock versions
                        foreach (T b in FindInterfaceImplementors(Subfolders))
                            interfaces[GetInterfaceKey(b)] = b;

                        m_interfaces = interfaces;
                    }
        }

        /// <summary>
        /// Searches the base folder of this dll for classes in other assemblies that 
        /// implements a certain interface, and has a default constructor
        /// </summary>
        /// <param name="additionalfolders">Any additional folders besides the assembly path to search in</param>
        /// <returns>A list of instanciated classes which implements the interface</returns>
        private List<T> FindInterfaceImplementors(string[] additionalfolders) 
        {
            List<T> interfaces = new List<T>();

            string path = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            List<string> files = new List<string>();
            files.AddRange(System.IO.Directory.GetFiles(path, "*.dll"));

            //We can override with subfolders
            if (additionalfolders != null)
                foreach (string s in additionalfolders)
                {
                    string subpath = System.IO.Path.Combine(path, s);
                    if (System.IO.Directory.Exists(subpath))
                        files.AddRange(System.IO.Directory.GetFiles(subpath, "*.dll"));
                }

            foreach (string s in files)
            {
                try
                {
                    //NOTE: This is pretty nifty, due to the use of assembly redirect and LoadFile, we can
                    // actually end up loading multiple versions of the same file (if it is present).
                    //Since the lookup dictionary applies the modules in the order returned
                    // and the subfolders are probed last, a module in the subfolder
                    // will take the place of a stock module, if both use same key
                    System.Reflection.Assembly asm = System.Reflection.Assembly.LoadFile(s);
                    if (asm != System.Reflection.Assembly.GetExecutingAssembly())
                    {
                        foreach (Type t in asm.GetExportedTypes())
                            try
                            {
                                if (typeof(T).IsAssignableFrom(t) && t != typeof(T))
                                {
                                    //TODO: Figure out how to support types with no default constructors
                                    if (t.GetConstructor(Type.EmptyTypes) != null)
                                    {
                                        T i = Activator.CreateInstance(t) as T;
                                        if (i != null)
                                            interfaces.Add(i);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                ex = GetActualException(ex);
                                Duplicati.Library.Logging.Log.WriteWarningMessage(LOGTAG, "SoftError", ex, Strings.DynamicLoader.DynamicTypeLoadError(t.FullName, s, ex.Message));
                            }
                    }
                }
                catch(Exception ex)
                {   
                    ex = GetActualException(ex);
                    // Since this is locating the assemblies that have the proper interface, it isn't an error to not.
                    // This was loading the log with errors about additional DLL's that are not plugins and do not have manifests.
                    Duplicati.Library.Logging.Log.WriteExplicitMessage(LOGTAG, "HardError", ex, Strings.DynamicLoader.DynamicAssemblyLoadError(s, ex.Message));
                }
            }

            return interfaces;
        }

        private Exception GetActualException(Exception ex)
        {
            if (ex is TargetInvocationException)
                ex = (ex as TargetInvocationException).InnerException;
            return ex;
        }

        /// <summary>
        /// Gets a list of loaded interfaces
        /// </summary>
        public T[] Interfaces
        {
            get
            {
                LoadInterfaces();
                lock(m_lock)
                    return new List<T>(m_interfaces.Values).ToArray();
            }
        }

        /// <summary>
        /// Gets a list of the keys of loaded interfaces
        /// </summary>
        public string[] Keys
        {
            get
            {
                LoadInterfaces();
                lock (m_lock)
                    return new List<string>(m_interfaces.Keys).ToArray();
            }
        }

    }
}

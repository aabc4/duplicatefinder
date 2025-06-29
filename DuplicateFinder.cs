//"C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\Roslyn\csc.exe" DuplicateFinder.cs -lib:"C:\windows\microsoft.net\framework\v4.0.30319" -r:microsoft.visualbasic.dll -r:system.runtime.dll

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using System.Runtime.InteropServices;
using static dupfind.myserialization;


namespace dupfind {
    class DuplicateFinder {
        public static string helptext =
@"usage: duplicatefinder.exe -enumerate C:\somefolder .\outputfile.xml
   surveys a folder and saves hashes of the files to outputfile.xml
usage: duplicatefinder.exe -enumerate STDIN .\outputfile.xml
   reads a list of files from standard input (in UTF8 encoding)
   and saves hashes of the files to outputfile.xml
usage: duplicatefinder.exe -resume
   resumes an -enumerate task that was interrupted
usage: duplicatefinder.exe -enumerate C:\somefolder .\outputfile.xml -deduplicate <deletenow | outfile.txt>
   surveys a folder
   deletes duplicate files in the folder
   produces outputfile.xml, listing only the files that aren't deleted
      deletenow sends files directly to recycle bin
      outfile.txt saves a list of delete commands instead of deleting
usage: duplicatefinder.exe .\somelist.xml -deduplicate <deletenow | outfile.txt>
   deletes duplicate files from list
   produces somelist-new.xml, listing only the files that aren't removed
      deletenow sends files directly to recycle bin
      outfile.txt saves a list of delete commands instead of deleting
usage: duplicatefinder.exe .\somelist.xml -removefrom .\otherlist.xml <deletenow | outfile.txt>
   deletes files present in somelist.xml from otherlist.xml
   produces otherlist-new.xml, listing only the files that aren't removed
      deletenow sends files directly to recycle bin
      outfile.txt saves a list of delete commands instead of deleting
usage: duplicatefinder.exe .\somelist.xml -splitfolder ""a:\b\c"" .\splitlist.xml
   takes xml file produced by -enumerate and splits it into two:
      splitlist.xml lists the files whose path starts with a:\b\c
      somelist-new.xml lists the remaining files from somelist.xml
usage: duplicatefinder.exe .\somelist.xml -merge .\otherlist.xml .\mergedlist.xml
   takes two xml files and combines their contents into mergedlist.xml
usage: duplicatefinder.exe .\somelist.xml -restoredates ""a:\original\folder"" ""B:\new\folder""
   replaces date metadata of files in B:\new\folder with dates of corresponding
   identical files from A:\original\folder , as listed in somelist.xml";
        static readonly string progress1chars = "|/-\\";
        static int progress1state = 0;
        static string progressTitle = "";
        static long clock = DateTime.Now.Ticks;
        static long howlong() { 
            long f = clock; clock = DateTime.Now.Ticks; return (clock - f)/100000;
        }
        static void showdelay(string x = "") {
            decimal y = (decimal)howlong() / 100;
            string q = y.ToString() + " s";
            if (y>3600) q = $"{(int)(y / 3600)}:{(int)((y / 60) % 60)}:{y % 60} s";
            if (y > 300) q = $"{(int)(y / 60)}:{y % 60} s";
            Console.WriteLine($"{x} {q} ");
        }
        static void progress1() {
            if(!Console.IsOutputRedirected) Console.CursorLeft = 0;
            Console.Write($"{progressTitle}{progress1chars[progress1state]} ");
            progress1state += 1;
            if (progress1state >= progress1chars.Length) progress1state = 0;
        }
        static int currentStep = 0; static int totalSteps = 0;
            static int progressBarWidth = 30;
        static void progress2reset(int totalsteps, string title = "") {
            currentStep = 1; totalSteps = totalsteps;
            if (!String.IsNullOrEmpty(title)) { 
                progressTitle = $"{title} : ";
                } else progressTitle = "";
        }
        static void progress2(){
            double progress = (double)currentStep / totalSteps;
            int progressChars = (int)(progress * progressBarWidth);
            string progressBar = new string('#', progressChars) + new string('-', progressBarWidth - progressChars);
            if(!Console.IsOutputRedirected) Console.CursorLeft = 0;
            Console.Write($"{progressTitle}[{progressBar}] {currentStep++}/{totalSteps} ");// ({progress * 100:0.0}%)");
        }
        public static List<string> ReadLinesFromStdIn() {
            var lines = new List<string>();
            var ytr = Console.OpenStandardInput();
            string? line;
            using (var qy = new StreamReader(ytr, Encoding.UTF8)) {
            while ((line = qy.ReadLine()) != null) lines.Add(line);
            };
            ytr.Dispose();
            return lines;
        }
        static StringBuilder changes = new StringBuilder();
        static bool deletenow = false;
        static List<myfile> getfile(string file) => deserializefromfile<List<myfile>>(file);
        static void tryrecycle(myfile fdat) {
            if (!deletenow) { changes.Append($"recycle-item \"{fdat.fileinst.fullname}\"\r\n"); }
            else try {  Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(fdat.fileinst.fullname,            
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,                
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin); 			    
            } catch {Console.WriteLine($"failed to recycle {fdat.fileinst.fullname}"); }; }
        static void dumpoutput(string filename) { 
                if(!deletenow) File.WriteAllText(filename, changes.ToString(), Encoding.UTF8);}

        static void Main(string[] args) {
            //sort by: biggest file, least deep directory, shortest file name
            IComparer<myfile> sortbysizeandlengthofname = Comparer<myfile>.Create(
                (myfile a, myfile b) => {return (int)(b.filedata.length - a.filedata.length + 
                    a.fileinst.path.Count((c)=>c=='\\') - b.fileinst.path.Count((c) => c == '\\') + 
                    a.fileinst.name.Length - b.fileinst.name.Length);});
            clock = DateTime.Now.Ticks;
            if (args.Length < 1) { Console.WriteLine(helptext); return; };
            deletenow = "deletenow" == args[args.Length - 1].ToLower();
            var myfiledatas = new HashSet<myfiledata>(); var myfileinstances = new List<myfileinstance>();

            Action<IList<myfile>,string> deduplicate = (alist,outfile) => { 
                var newlist = new List<myfile>();
                myfiledatas = new HashSet<myfiledata>();
                int tally = 0; long bytesfreed = 0;
                progress2reset(alist.Count, "Deduplicating");
                foreach (myfile fdat in alist) {
                    progress2();
                    if (myfiledatas.Add(fdat.filedata)) { newlist.Add(fdat); }
                    else { tally++; bytesfreed += fdat.filedata.length; tryrecycle(fdat); }; };
                dumpoutput(args[args.Length - 1]);
                serializetofile(newlist, outfile);
                showdelay();
                Console.WriteLine($"found {tally} identical files - {bytesfreed:N0} bytes ");
                Console.WriteLine("saving output "); return; 
            };
            if (args.Contains("-enumerate")) {
                //usage: duplicatefinder.exe -enumerate C:\somefolder .\outputfile.xml [-deduplicate <deletenow | outfile.txt>]
                var outfile = args[2]; var thefolder = args[1];
                var newlist = new List<myfile>();
                progressTitle = "";
                Console.Write("Listing files... ");
                IEnumerable<string> allfiles = (String.Equals("STDIN", thefolder.ToUpper())
                    ? ReadLinesFromStdIn()
                    : SafeWalk.EnumerateFiles(thefolder, "*", SearchOption.AllDirectories, progress1));
                //reminder: Enumerables are late-evaluated
                var yy = allfiles.Count();
                showdelay();
                progress2reset(yy, "Hashing      ");
                long lastsave = DateTime.Now.Ticks;
                int i = 0;
                savetask(allfiles, args);
                foreach (string afile in allfiles) {
                    progress2();
                    newlist.Add(new myfile(new FileInfo(afile)));
                    i++;
                    trysavetaskprogress(i, newlist, ref lastsave);
                };
                showdelay();
                newlist.Sort(sortbysizeandlengthofname);
                if (args.Contains("-deduplicate")) {
                    deduplicate(newlist, outfile); return;
                }
                serializetofile(newlist, outfile);
                Console.WriteLine("saving output "); return;
            }
            if (args.Contains("-resume")) {
                //usage: duplicatefinder.exe -resume
                resumetask(out IEnumerable<string> allfiles, out args, out int i, out List<myfile> newlist);
                var outfile = args[args.Length - 1]; var thefolder = args[args.Length - 2];
                var yy = allfiles.Count();
                showdelay("Resuming... ");
                progress2reset(yy, "Hashing      ");
                currentStep = i;
                long lastsave = DateTime.Now.Ticks;
                savetask(allfiles, args);
                while(i<yy) {
                    progress2();
                    newlist.Add(new myfile(new FileInfo(allfiles.ElementAt(i))));
                    i++;
                    trysavetaskprogress(i, newlist, ref lastsave); 
                };
                showdelay();
                newlist.Sort(sortbysizeandlengthofname);
                if (args.Contains("-deduplicate")) {
                    deduplicate(newlist, outfile); return;
                }
                serializetofile(newlist, outfile);
                Console.WriteLine("saving output "); return;
            }
            if (args.Contains("-deduplicate")) {//usage: duplicatefinder.exe somelist.xml -deduplicate <deletenow | outfile.txt>
                var oldlist = getfile(args[0]);
                deduplicate(oldlist, $"{args[0]}-new.xml"); return;
            }                        
            if (args.Contains("-restoredates")){
            //usage: duplicatefinder.exe .\somelist.xml -restoredates "a:\original\folder" "B:\new\folder"
                var infolder = args[args.Length - 2]; 
                var outfolder = args[args.Length - 1];
                var inlist = getfile(args[0]); 
                var i = 0;
                foreach (myfile fdat in inlist) {
                    var newfile = fdat.fileinst.path.Replace(infolder, outfolder)+"\\"+fdat.fileinst.name;                    
                    var t = new FileInfo(newfile);
                    if(t.Exists) {
                        try {  File.SetCreationTime(newfile, fdat.fileinst.creationtime);
                            File.SetLastAccessTime(newfile, fdat.fileinst.lastaccesstime);
                            File.SetLastWriteTime(newfile, fdat.fileinst.lastwritetime);
                            i += 1; } catch { Console.WriteLine($"failed to update {fdat.fileinst.fullname}"); }; } }
                Console.WriteLine($"restored {i}/{inlist.Count} file dates"); return;
            }
            if (args.Contains("-removefrom")) {
            //usage: duplicatefinder.exe .\somelist.xml -removefrom .\otherlist.xml <deletenow | outfile.txt>
                var outfile = args[args.Length - 2];
                var oldlist = getfile(args[0]);
                progress2reset(oldlist.Count, "Deduplicating");
                foreach (myfile fdat in oldlist) {
                    progress2();
                    myfiledatas.Add(fdat.filedata);
                }
                var otherlist = getfile(args[args.Length - 2]);
                otherlist.Sort(sortbysizeandlengthofname);
                var firstlist = new List<myfile>(); 
                progress2reset(otherlist.Count);
                foreach (myfile fdat in otherlist) { 
                    progress2();
                    if (myfiledatas.Contains(fdat.filedata)) { tryrecycle(fdat); }
                    else firstlist.Add(fdat); }
                serializetofile(firstlist, $"{outfile}-new.xml"); 
                dumpoutput(args[args.Length - 1]); return;
            }
            if (args.Contains("-merge")) {//usage: duplicatefinder.exe .\somelist.xml -merge .\otherlist.xml .\mergedlist.xml
                var outfile = args[args.Length - 1];
                var outlist = getfile(args[0]).Concat(getfile(args[args.Length - 2])).ToList();
                outlist.Sort(sortbysizeandlengthofname);
                serializetofile(outlist, outfile); return;
            }
            if (args.Contains("-splitfolder")) {//usage: duplicatefinder.exe .\somelist.xml -splitfolder "a:\b\c" .\splitlist.xml
                var outfile = args[0];
                var outlist = getfile(args[0]);
                var splitlist = outlist.Where(c => c.fileinst.path.StartsWith(args[2])).ToList();
                outlist.RemoveAll(c => c.fileinst.path.StartsWith(args[2]));
                serializetofile(outlist, $"{outfile}-new.xml");
                serializetofile(splitlist, args[3]); return;
            }
            Console.WriteLine(helptext);
            //var shell =  new IWshRuntimeLibrary.WshShell(); IWshRuntimeLibrary.WshShortcut q;
            //linkcmd = (src, tgt) => { q = shell.CreateShortcut(src + ".lnk"); q.TargetPath = tgt; q.Save(); };
            //if (args.Contains("-winshortcuts")) linkcmd = (src, tgt) => 
            //    $"powershell -executionpolicy bypass -nologo -noninteractive -noprofile -command \"$ws = New-Object -ComObject WScript.Shell; $S = $ws.CreateShortcut('{src}.lnk'); $S.TargetPath = '{tgt}'; $S.Save()\"";            
        }
        private static void resumetask(out IEnumerable<string> allfiles, out string[] args, out int i, out List<myfile> newlist) {
            allfiles = deserializefromfile<List<string>>("allfiles.tmp");
            args = deserializefromfile<string[]>("args.tmp");
            i = deserializefromfile<int>("i.tmp");
            newlist = getfile("newlist.tmp");
        }        
        //save current progress every 100 seconds approximately
        private static void trysavetaskprogress(int i, List<myfile> newlist, ref long clock) {
            if (((DateTime.Now.Ticks - clock) >> 23) < 120) return;
            clock = DateTime.Now.Ticks; 
            serializetofile(i, "i.tmp");
            serializetofile(newlist, "newlist.tmp");
        }
        private static void savetask(IEnumerable<string> allfiles, string[] args) {
            serializetofile(allfiles.ToList(), "allfiles.tmp");
            serializetofile(args, "args.tmp");
        }
    }
    public class myfile {
        public myfileinstance fileinst; public myfiledata filedata;
        public myfile(FileInfo fi) { fileinst = new myfileinstance(fi); filedata = new myfiledata(fi); }
        public myfile() { }
    }
    public static class myserialization {
        public static int hashIStructEq<T>(this IList<T> list) =>
            ((IStructuralEquatable)list).GetHashCode(EqualityComparer<T>.Default);

        public static T deserializefromfile<T>(string filename) {
            T res;
            var aserializer = new XmlSerializer(typeof(T));
            using (Stream fs = readsafe(filename))
                res = (T)aserializer.Deserialize(fs);
            return res;
        }
        public static void serializetofile(object qtre, string filename) {
            var aserializer = new XmlSerializer(qtre.GetType());
            var nvq = new XmlWriterSettings() {
                Indent = true,
                Encoding = Encoding.Unicode,
                NewLineOnAttributes = true,
                NewLineChars = "\r\n",
                NewLineHandling = NewLineHandling.Entitize
            };
            using (Stream fs = new FileStream(filename, FileMode.Create, FileAccess.ReadWrite))
            using (XmlWriter writer = XmlWriter.Create(fs, nvq)) //new XmlTextWriter(fs, Encoding.Unicode))
                aserializer.Serialize(writer, qtre);
        }
        public static System.IO.FileStream readsafe(string a) =>
            new System.IO.FileStream(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    }
    public class myfiledata {
        private static readonly Func<string, string> hexToB32 = c => rfc4648.bytestobase32hex(rfc4648.base16tobytes(c));
        private static readonly Func<string, string> firsttwochars = c => hexToB32(c).Substring(0, 2);
        public string[] hashes; public string md4 => hashes[0];
        public string md5 => hashes[1]; public string sha1 => hashes[2];
        public string sha224 => hashes[3]; public string sha256 => hashes[4];
        public string sha384 => hashes[5]; public string sha512 => hashes[6];
        public string ext; //note that ext is completely ignored in equality comparisons
        public string uniquename => hexToB32(hashes[2]);
        public string uniquepath => firsttwochars(sha256) + "\\" + firsttwochars(sha512);
        public string fulluniquelocator => uniquepath + "\\" + uniquename + "-" + length.ToString() + ext;
        public long length;
        public myfiledata() { hashes = new string[7]; }
        public myfiledata(FileInfo theinfo) {
            hashes = new string[7];
            ext = theinfo.Extension;
            try { length = theinfo.Length; } catch { return; }
            Process rhash = new Process();
            rhash.StartInfo.FileName = $"rhash.exe"; //program that concurrently calculates several hashes of the same data
            //rhash.StartInfo.Arguments = $"--uppercase --md4 -M -H --sha224 --sha256 --sha384 --sha512 \"{theinfo.FullName}\"";
            //the dash at the end means "read from standard input"
            rhash.StartInfo.Arguments = $"--uppercase --md4 -M -H --sha224 --sha256 --sha384 --sha512 -";
            rhash.StartInfo.UseShellExecute = false;
            rhash.StartInfo.RedirectStandardOutput = true;
            rhash.StartInfo.RedirectStandardInput = true;
            rhash.Start();
            var digest = digestbytes(theinfo.FullName);
            rhash.StandardInput.BaseStream.Write(digest, 0, digest.Length);
            rhash.StandardInput.Close();
            rhash.StandardOutput.ReadToEnd().
                TrimEnd(new char[] { '\r', '\n' }). //spurious newlines caused some lossage
                Split(new char[1] { ' ' }, StringSplitOptions.RemoveEmptyEntries).
                Reverse().Take(7).Reverse().ToArray().CopyTo(hashes, 0);
            rhash.WaitForExit();
            //try { File.Delete(tempfile); } 
            //catch(UnauthorizedAccessException) { Console.WriteLine($"failed to delete {tempfile}"); }
            //catch(Exception) { Console.WriteLine($"failed to delete {tempfile}"); }
        }

        public override int GetHashCode() => hashes.hashIStructEq() ^ length.GetHashCode();
        //{ return ((IStructuralEquatable)hashes).GetHashCode(EqualityComparer<string>.Default) ^ length.GetHashCode(); }
        //{ return th.Aggregate(0, (hash,s) => { return hash ^ s.GetHashCode(); }) ^ length.GetHashCode(); }
        //{ return 9; }
        public override bool Equals(object obj) {
            if ((obj == null) || !this.GetType().Equals(obj.GetType())) return false;
            if (ReferenceEquals(this, obj)) return true;
            return Equals((myfiledata)obj);
        }
        public bool Equals(myfiledata obj) {
            return (length == obj.length) && hashes.SequenceEqual(obj.hashes);
        }
        public static byte[] digestbytes(string inputFilePath) {
            if (!File.Exists(inputFilePath)) return default;
            long fileSize = new FileInfo(inputFilePath).Length;
            double logBase = 1.0003;
            int N = (int)Math.Floor(Math.Log(fileSize) / Math.Log(logBase));
            var fi = new FileInfo(inputFilePath);
            string tempFilePath = $"R:\\{Guid.NewGuid()}";
            if (fi.Length <= (1 + N + N))
                try { return File.ReadAllBytes(inputFilePath); }
                catch (Exception) {
                    Console.WriteLine($"unable to read file {inputFilePath}");
                    return default;
                }
            byte[] res = new byte[N + N + 1];
            try {
                using (FileStream fs = myserialization.readsafe(inputFilePath)) {
                    fs.Read(res, 0, N);
                    fs.Seek(fileSize / 2, SeekOrigin.Begin);
                    res[N] = (byte)fs.ReadByte();
                    fs.Seek(-N, SeekOrigin.End);
                    fs.Read(res, N + 1, N);
                } } 
            catch (Exception) {
                Console.WriteLine($"skipping file (already in use?): {inputFilePath}");
                return default;
            }
            return res;
        }
    }

    public class myfileinstance {
        public string[] th;
        public string name => th[0];
        public string path => th[1];
        public string fullname => path + '\\' + name;
        //public myfiledata thedata;
        public DateTime[] times;
        public DateTime creationtime => times[0];
        public DateTime lastaccesstime => times[1];
        public DateTime lastwritetime => times[2];

        public myfileinstance() { }
        public myfileinstance(FileInfo theinfo) {
            th = new string[2]; times = new DateTime[3];
            th[0] = theinfo.Name;
            th[1] = theinfo.DirectoryName;
            times[0] = theinfo.CreationTime;
            times[1] = theinfo.LastAccessTime;
            times[2] = theinfo.LastWriteTime;
        }
        public myfileinstance(string thepath) {
            new myfileinstance(new FileInfo(thepath));
        }

        public override int GetHashCode() => th.hashIStructEq() ^ times.hashIStructEq();
        //{ return ((IStructuralEquatable)th).GetHashCode(EqualityComparer<string>.Default) ^
        //  ((IStructuralEquatable)times).GetHashCode(EqualityComparer<DateTime>.Default); }
        public override bool Equals(object obj) {
            return Equals(obj as myfileinstance);
        }
        public bool Equals(myfileinstance obj) {
            if (ReferenceEquals(obj, null)) return false;
            if (ReferenceEquals(this, obj)) return true;
            return times.SequenceEqual(obj.times) && th.SequenceEqual(obj.th);
        }
    }

    // https://stackoverflow.com/a/5957525
    public static class SafeWalk {
        public static IEnumerable<string> EnumerateFiles(
            string path, string searchPattern, SearchOption searchOpt, Action progresscallback = default) {
            try {
                var dirFiles = Enumerable.Empty<string>();
                if (searchOpt == SearchOption.AllDirectories) {
                    dirFiles = Directory.EnumerateDirectories(path)
                                        .SelectMany(x => { progresscallback(); 
                                            return EnumerateFiles(x, searchPattern, searchOpt, progresscallback); });
                };
                if (progresscallback != default) progresscallback();
                return dirFiles.Concat(Directory.EnumerateFiles(path, searchPattern));
            } 
            catch (UnauthorizedAccessException) {
                Console.WriteLine($"forbidden: {path}");
                return Enumerable.Empty<string>();
            }
            catch (IOException) {
                Console.WriteLine($"unreadable: {path}");
                return Enumerable.Empty<string>();
            }
        }
    }
    public class rfc4648 {
        private static readonly string base32alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567=";
        private static readonly string base32hexalphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUV=";
        private static byte get5bits(int pos, byte fb, byte sb) {
            switch (pos) {
                case 0: return (byte)((fb & 0b11111000) >> 3);
                case 1: return (byte)((fb & 0b01111100) >> 2);
                case 2: return (byte)((fb & 0b00111110) >> 1);
                case 3: return (byte)((fb & 0b00011111) >> 0);
                case 4: return (byte)(((fb & 0b00001111) << 1) + ((sb & 0b10000000) >> 7));
                case 5: return (byte)(((fb & 0b00000111) << 2) + ((sb & 0b11000000) >> 6));
                case 6: return (byte)(((fb & 0b00000011) << 3) + ((sb & 0b11100000) >> 5));
                case 7: return (byte)(((fb & 0b00000001) << 4) + ((sb & 0b11110000) >> 4));
            }
            return 0;
        }
        private static string btostr32(byte[] thebytes, string alphabet) {
            int cap = thebytes.Length * 8;
            byte[] outbytes = new byte[((cap / 40) * 8 + (cap % 40 > 0 ? 8 : 0))];
            int i;
            for (i = 0; cap > i * 5; i++)
                outbytes[i] = (byte)alphabet[get5bits(
                     (i * 5) & 7,
                     thebytes[(i * 5) >> 3],
                     (1 + ((i * 5) >> 3)) >= thebytes.Length ?
                        (byte)0 :
                        thebytes[1 + ((i * 5) >> 3)])];
            for (; i < outbytes.Length; i++) outbytes[i] = (byte)alphabet[alphabet.Length - 1];
            return System.Text.Encoding.Default.GetString(outbytes);
        }

        public static string bytestobase32(byte[] thebytes) {
            return btostr32(thebytes, base32alphabet);
        }
        public static byte[] base32tobytes(string thestring) {
            return new byte[] { 0 };
        }
        public static string bytestobase32hex(byte[] thebytes) {
            return btostr32(thebytes, base32hexalphabet);
        }
        public static byte[] base32hextobytes(string thestring) {
            return new byte[] { 0 };
        }
        public static byte[] base16tobytes(string hex) {
            if (hex.Length % 2 == 1)
                throw new Exception("The binary key cannot have an odd number of digits");
            byte[] arr = new byte[hex.Length >> 1];
            for (int i = 0; i < hex.Length >> 1; ++i)
                arr[i] = (byte)((GetHexVal(hex[i << 1]) << 4) + (GetHexVal(hex[(i << 1) + 1])));
            return arr;
        }
        public static int GetHexVal(char hex) {
            int val = (int)hex;
            return val - (val < 58 ? 48 : (val < 97 ? 55 : 87));
        }
    }

    [ComImport, Guid("13709620-C279-11CE-A49E-444553540000")]
    class Shell32 { }

    [ComImport, Guid("D8F015C0-C278-11CE-A49E-444553540000")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    public interface IShellDispatch {
        [DispId(0x60020007)]//01 00 07 00 02 60 00 00
        void MinimizeAll();
        [DispId(0x60020002)] Folder NameSpace(int x);
    }

    [ComImport, Guid("BBCBDE60-C3FF-11CE-8350-444553540000")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    public interface Folder {
        [DispId(0x60020007)]
        void MoveHere(string h, int q);
        [DispId(0)] string Title { get; }
    }
    //[ComImport,Guid("F0D2D8EF-3890-11D2-BF8B-00C04FB93661")]
    //public class Folder2{}
    sealed class MyRecycler : IDisposable {
        Shell32 shell;
        IShellDispatch shellDispatch;
        Folder recycler;
        public MyRecycler() {
            shell = new Shell32();
            shellDispatch = (IShellDispatch)shell;
            recycler = shellDispatch.NameSpace(10);
        }
        public void recycle(string filename) {
            if (recycler == null)
                throw new ObjectDisposedException("Folder-recyclebin");
            recycler.MoveHere(Path.GetFullPath(filename), 0);
        }
        public void MinimizeAll() {
            if (shellDispatch == null)
                throw new ObjectDisposedException("Shell");
            shellDispatch.MinimizeAll();
        }
        public void Dispose() {
            try {
                if (shellDispatch != null) Marshal.ReleaseComObject(shellDispatch);
                if (shell != null) Marshal.ReleaseComObject(shell);
                if (recycler != null) Marshal.ReleaseComObject(recycler);
            }
            finally {
                shell = null;
                shellDispatch = null;
                recycler = null;
                GC.SuppressFinalize(this);
            }
        }
    }
}

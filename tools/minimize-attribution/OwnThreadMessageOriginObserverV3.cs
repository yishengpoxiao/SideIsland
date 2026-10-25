using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace CodexIsland
{
    // Diagnostic-only own-thread observation. Never a global hook or injector.
    // Caller supplies an exact owned-HWND predicate and a bounded file sink.
    internal sealed class OwnThreadMessageOriginObserverV3 : IDisposable
    {
        internal sealed class Observation
        {
            public string Event, Hwnd;
            public long Qpc, QpcFrequency, QueueOrdinal;
            public uint ReceiverProcessId, CurrentThreadId, MessageTimeMs;
            public int HookCode, QueueDisposition;
            public uint? Message;
            public string W, L;
            public int? X, Y, HitTest;
            public bool MessageFieldsSuppressed;
            public object InputSource;
            public uint? MessageTypeId;
            public long CallSpanId,ParentCallSpanId,QueueOrdinalAtCallEntry;
            public int CallDepth;
            public string RawHookWParam,MessageResult;
            public bool HookSourceFlagNonzero;
            public uint InSendMessageEx;
        }
        [StructLayout(LayoutKind.Sequential)] private struct Point { internal int X,Y; }
        [StructLayout(LayoutKind.Sequential)] private struct MessageData
        { internal IntPtr Hwnd;internal uint Message;internal IntPtr W,L;internal uint Time;internal Point Point;internal uint Private; }
        [StructLayout(LayoutKind.Sequential)] private struct MouseData
        { internal Point Point;internal IntPtr Hwnd;internal uint HitTest;internal UIntPtr ExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] private struct BeforeData
        { internal IntPtr L,W;internal uint Message;internal IntPtr Hwnd; }
        [StructLayout(LayoutKind.Sequential)] private struct AfterData
        { internal IntPtr Result,L,W;internal uint Message;internal IntPtr Hwnd; }
        private sealed class CallFrame
        { internal long Id,Parent,QueueAtEntry;internal int Depth;internal IntPtr Hwnd,W,L;internal uint Message; }
        [StructLayout(LayoutKind.Sequential)] private struct Source { internal uint DeviceType,OriginId; }
        private delegate IntPtr HookProc(int code,IntPtr w,IntPtr l);
        [DllImport("user32.dll",SetLastError=true)] private static extern IntPtr SetWindowsHookEx(int id,HookProc callback,IntPtr module,uint thread);
        [DllImport("user32.dll",SetLastError=true)] private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr w,IntPtr l);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint process);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll",SetLastError=true)] private static extern bool GetCurrentInputMessageSource(out Source source);
        [DllImport("user32.dll")] private static extern uint InSendMessageEx(IntPtr reserved);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        private readonly Func<IntPtr,bool> allowed;
        private readonly Action<Observation> sink;
        private readonly uint pid,thread;
        private readonly HookProc queueDelegate,mouseDelegate,beforeDelegate,afterDelegate;
        private IntPtr queueHook,mouseHook,beforeHook,afterHook;
        private readonly Stack<CallFrame> calls=new Stack<CallFrame>();
        private long callSequence;
        private bool disposed;
        private long ordinal;
        internal int CallbackErrors { get; private set; }
        internal int CleanupErrors { get; private set; }
        internal long QueueRecords { get; private set; }
        internal long MouseRecords { get; private set; }
        internal long CallEntryRecords { get; private set; }
        internal long CallReturnRecords { get; private set; }
        internal int CallPairingErrors { get; private set; }
        internal int ActiveCallSpans { get { return calls.Count; } }
        private OwnThreadMessageOriginObserverV3(Func<IntPtr,bool> ownTarget,Action<Observation> output)
        {
            if(ownTarget==null || output==null)throw new ArgumentNullException();
            allowed=ownTarget;sink=output;
            using(var process=Process.GetCurrentProcess())pid=(uint)process.Id;
            thread=GetCurrentThreadId();
            if(thread==0)throw new InvalidOperationException("Own native thread is required; global thread zero is forbidden.");
            queueDelegate=Queue;mouseDelegate=Mouse;beforeDelegate=Before;afterDelegate=After;
            queueHook=SetWindowsHookEx(3,queueDelegate,IntPtr.Zero,thread);
            if(queueHook==IntPtr.Zero)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"own WH_GETMESSAGE");
            mouseHook=SetWindowsHookEx(7,mouseDelegate,IntPtr.Zero,thread);
            if(mouseHook==IntPtr.Zero){int error=Marshal.GetLastWin32Error();UnhookWindowsHookEx(queueHook);queueHook=IntPtr.Zero;throw new System.ComponentModel.Win32Exception(error,"own WH_MOUSE");}
            beforeHook=SetWindowsHookEx(4,beforeDelegate,IntPtr.Zero,thread);
            if(beforeHook==IntPtr.Zero){int error=Marshal.GetLastWin32Error();Dispose();throw new System.ComponentModel.Win32Exception(error,"own WH_CALLWNDPROC");}
            afterHook=SetWindowsHookEx(12,afterDelegate,IntPtr.Zero,thread);
            if(afterHook==IntPtr.Zero){int error=Marshal.GetLastWin32Error();Dispose();throw new System.ComponentModel.Win32Exception(error,"own WH_CALLWNDPROCRET");}
        }
        internal static OwnThreadMessageOriginObserverV3 Attach(Func<IntPtr,bool> ownTarget,Action<Observation> boundedSink)
        { return new OwnThreadMessageOriginObserverV3(ownTarget,boundedSink); }
        private bool Own(IntPtr hwnd)
        {
            if(hwnd==IntPtr.Zero || disposed || GetCurrentThreadId()!=thread)return false;
            uint process;uint actualThread=GetWindowThreadProcessId(hwnd,out process);
            return process==pid && actualThread==thread && IsWindow(hwnd) && allowed(hwnd);
        }
        private IntPtr Queue(int code,IntPtr w,IntPtr l)
        {
            long qpc=Stopwatch.GetTimestamp();
            try
            {
                if(code>=0 && l!=IntPtr.Zero)
                {
                    var msg=(MessageData)Marshal.PtrToStructure(l,typeof(MessageData));
                    if(Own(msg.Hwnd))
                    {
                        // Every owned queue callback gets an ordinal, including
                        // PM_NOREMOVE. No text/keys or unknown-message parameters.
                        var record=CreateQueue(msg.Hwnd,msg.Message,msg.W,msg.L,msg.Time,code,w.ToInt32(),++ordinal,qpc,pid,thread);
                        record.InputSource=Whitelisted(msg.Message)?Input():null;
                        sink(record);QueueRecords++;
                    }
                }
            }
            catch{CallbackErrors++;}
            return CallNextHookEx(queueHook,code,w,l);
        }
        private IntPtr Mouse(int code,IntPtr w,IntPtr l)
        {
            long qpc=Stopwatch.GetTimestamp();
            try
            {
                uint message=unchecked((uint)w.ToInt64());
                // Only actual button events for this own HWND; no mouse moves,
                // wheel stream, other windows or global input are recorded.
                if(code>=0 && l!=IntPtr.Zero && ((message>=0xA1 && message<=0xA9) || (message>=0x201 && message<=0x209)))
                {
                    var data=(MouseData)Marshal.PtrToStructure(l,typeof(MouseData));
                    if(Own(data.Hwnd))
                    {
                        sink(new Observation{Event="OWN_THREAD_MOUSE_BUTTON",Hwnd=Hex(data.Hwnd),Qpc=qpc,QpcFrequency=Stopwatch.Frequency,
                            ReceiverProcessId=pid,CurrentThreadId=thread,HookCode=code,Message=message,X=data.Point.X,Y=data.Point.Y,HitTest=unchecked((int)data.HitTest),InputSource=Input()});
                        MouseRecords++;
                    }
                }
            }
            catch{CallbackErrors++;}
            return CallNextHookEx(mouseHook,code,w,l);
        }
        private Observation CallRecord(string kind,CallFrame frame,int code,IntPtr rawFlag,long qpc,uint ismex)
        {
            var record=CreateQueue(frame.Hwnd,frame.Message,frame.W,frame.L,0,code,0,0,qpc,pid,thread);
            record.Event=kind;record.MessageTypeId=frame.Message;record.CallSpanId=frame.Id;record.ParentCallSpanId=frame.Parent;record.CallDepth=frame.Depth;record.QueueOrdinalAtCallEntry=frame.QueueAtEntry;
            record.RawHookWParam=Hex(rawFlag);record.HookSourceFlagNonzero=rawFlag!=IntPtr.Zero;record.InSendMessageEx=ismex;
            record.InputSource=Whitelisted(frame.Message)?Input():null;return record;
        }
        private IntPtr Before(int code,IntPtr w,IntPtr l)
        {
            long qpc=Stopwatch.GetTimestamp();uint ismex=InSendMessageEx(IntPtr.Zero);
            try
            {
                if(code>=0&&l!=IntPtr.Zero)
                {
                    var data=(BeforeData)Marshal.PtrToStructure(l,typeof(BeforeData));
                    if(Own(data.Hwnd))
                    {
                        if(calls.Count>=1024)throw new InvalidOperationException("bounded own call stack");
                        var frame=new CallFrame{Id=++callSequence,Parent=calls.Count==0?0:calls.Peek().Id,Depth=calls.Count+1,QueueAtEntry=ordinal,Hwnd=data.Hwnd,Message=data.Message,W=data.W,L=data.L};
                        calls.Push(frame);sink(CallRecord("OWN_THREAD_CALLWNDPROC_ENTER",frame,code,w,qpc,ismex));CallEntryRecords++;
                    }
                }
            }
            catch{CallbackErrors++;}
            return CallNextHookEx(beforeHook,code,w,l);
        }
        private IntPtr After(int code,IntPtr w,IntPtr l)
        {
            long qpc=Stopwatch.GetTimestamp();uint ismex=InSendMessageEx(IntPtr.Zero);
            try
            {
                if(code>=0&&l!=IntPtr.Zero)
                {
                    var data=(AfterData)Marshal.PtrToStructure(l,typeof(AfterData));
                    // A matched own entry remains valid even if that WndProc
                    // destroyed its HWND before this return notification.
                    if(calls.Count>0&&calls.Peek().Hwnd==data.Hwnd&&calls.Peek().Message==data.Message&&calls.Peek().W==data.W&&calls.Peek().L==data.L)
                    {
                        var frame=calls.Pop();var record=CallRecord("OWN_THREAD_CALLWNDPROC_RETURN",frame,code,w,qpc,ismex);record.MessageResult=Whitelisted(data.Message)?Hex(data.Result):null;sink(record);CallReturnRecords++;
                    }
                    else if(Own(data.Hwnd)){CallPairingErrors++;}
                }
            }
            catch{CallbackErrors++;}
            return CallNextHookEx(afterHook,code,w,l);
        }
        internal static Observation CreateQueue(IntPtr hwnd,uint message,IntPtr w,IntPtr l,uint time,int code,int disposition,long sequence,long qpc,uint ownPid,uint ownThread)
        {
            bool safe=Whitelisted(message);
            return new Observation{Event="OWN_THREAD_GETMESSAGE",Hwnd=Hex(hwnd),Qpc=qpc,QpcFrequency=Stopwatch.Frequency,QueueOrdinal=sequence,ReceiverProcessId=ownPid,CurrentThreadId=ownThread,
                MessageTimeMs=time,HookCode=code,QueueDisposition=disposition,Message=safe?(uint?)message:null,W=safe?Hex(w):null,L=safe?Hex(l):null,MessageFieldsSuppressed=!safe};
        }
        private static bool Whitelisted(uint message)
        { return message==0x112 || message==5 || (message>=0xA1 && message<=0xA9); }
        private static object Input()
        {
            try{Source value;bool available=GetCurrentInputMessageSource(out value);return new {Available=available,DeviceType=value.DeviceType,OriginId=value.OriginId,ReceiverThreadContextOnly=true,DoesNotIdentifySender=true};}
            catch(Exception error){return new {Available=false,Error=error.GetType().Name};}
        }
        private static string Hex(IntPtr value){return "0x"+unchecked((ulong)value.ToInt64()).ToString("X");}
        public void Dispose()
        {
            if(disposed)return;
            if(GetCurrentThreadId()!=thread)throw new InvalidOperationException("Dispose on the attaching own thread.");
            disposed=true;
            if(queueHook!=IntPtr.Zero){if(!UnhookWindowsHookEx(queueHook))CleanupErrors++;queueHook=IntPtr.Zero;}
            if(mouseHook!=IntPtr.Zero){if(!UnhookWindowsHookEx(mouseHook))CleanupErrors++;mouseHook=IntPtr.Zero;}
            if(beforeHook!=IntPtr.Zero){if(!UnhookWindowsHookEx(beforeHook))CleanupErrors++;beforeHook=IntPtr.Zero;}
            if(afterHook!=IntPtr.Zero){if(!UnhookWindowsHookEx(afterHook))CleanupErrors++;afterHook=IntPtr.Zero;}
            GC.KeepAlive(queueDelegate);GC.KeepAlive(mouseDelegate);GC.KeepAlive(beforeDelegate);GC.KeepAlive(afterDelegate);
        }
    }
    internal static class MessageOriginObserverSelfCheckV3
    {
        private static int Main(string[] args)
        {
            if(args.Length!=1 || args[0]!="--self-check")return 2;
            var hidden=OwnThreadMessageOriginObserverV3.CreateQueue(new IntPtr(1),0x102,new IntPtr(0x41),new IntPtr(0x42),123,0,1,1,1,1,1);
            var sc=OwnThreadMessageOriginObserverV3.CreateQueue(new IntPtr(1),0x112,new IntPtr(0xF020),IntPtr.Zero,123,0,1,2,2,1,1);
            if(hidden.Message.HasValue || hidden.W!=null || hidden.L!=null || !hidden.MessageFieldsSuppressed || sc.Message!=0x112 || sc.W!="0xF020")return 3;
            Console.WriteLine("SELF-CHECK PASS; no UI or hook created; unknown message/WM_CHAR fields suppressed, SC numeric fields preserved.");return 0;
        }
    }
}

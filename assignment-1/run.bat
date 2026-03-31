csc /out:network\network.exe network\network.cs
csc /r:System.Windows.Forms.dll /out:Middleware1\Middleware1.exe Middleware1\Middleware1.cs
csc /r:System.Windows.Forms.dll /out:Middleware2\Middleware2.exe Middleware2\Middleware2.cs
csc /r:System.Windows.Forms.dll /out:Middleware3\Middleware3.exe Middleware3\Middleware3.cs
csc /r:System.Windows.Forms.dll /out:Middleware4\Middleware4.exe Middleware4\Middleware4.cs
csc /r:System.Windows.Forms.dll /out:Middleware5\Middleware5.exe Middleware5\Middleware5.cs
start network\network.exe
start Middleware1\Middleware1.exe
start Middleware2\Middleware2.exe
start Middleware3\Middleware3.exe
start Middleware4\Middleware4.exe
start Middleware5\Middleware5.exe

csc /out:network\network.exe network\network.cs 
csc /out:middleware8082\middleware8082.exe middleware8082\middleware8082.cs
csc /out:middleware8083\middleware8083.exe middleware8083\middleware8083.cs
csc /out:middleware8084\middleware8084.exe middleware8084\middleware8084.cs
csc /out:middleware8085\middleware8085.exe middleware8085\middleware8085.cs
csc /out:middleware8086\middleware8086.exe middleware8086\middleware8086.cs
start .\network\network.exe
start middleware8082\middleware8082.exe
start middleware8083\middleware8083.exe
start middleware8084\middleware8084.exe
start middleware8085\middleware8085.exe
start middleware8086\middleware8086.exe
---
uid: Uplink2.Runtime.MiniScript.MiniScriptSshIntrinsics
name: Module ssh
nameWithType: Module ssh
fullName: Module ssh
summary: <code>ssh</code> 모듈은 SSH 세션 연결/해제, 원격 명령 실행, 계정 정보 탐색 API를 제공합니다.
---

---
uid: Uplink2.Runtime.MiniScript.MiniScriptFtpIntrinsics
name: Module ftp
nameWithType: Module ftp
fullName: Module ftp
summary: <code>ftp</code> 모듈은 원격 파일 다운로드/업로드와 경로 기반 전송 API를 제공합니다.
---

---
uid: Uplink2.Runtime.MiniScript.MiniScriptNetIntrinsics
name: Module net
nameWithType: Module net
fullName: Module net
summary: <code>net</code> 모듈은 인터페이스 조회, 네트워크 스캔, 포트 목록/배너 조회 API를 제공합니다.
---

---
uid: Uplink2.Runtime.MiniScript.MiniScriptTermIntrinsics
name: Module term
nameWithType: Module term
fullName: Module term
summary: <code>term</code> 모듈은 명령 실행과 표준 출력/경고/에러 출력 API를 제공합니다.
---

---
uid: Uplink2.Runtime.MiniScript.MiniScriptSshIntrinsics.SshConnect
name: ssh.connect()
nameWithType: ssh.connect()
fullName: ssh.connect()
syntax:
  content: r = ssh.connect(hostOrIp, userId, password, port=22, opts?)
---
<a id="sshconnect"></a>

---
uid: Uplink2.Runtime.MiniScript.MiniScriptSshIntrinsics.SshDisconnect
name: ssh.disconnect()
nameWithType: ssh.disconnect()
fullName: ssh.disconnect()
syntax:
  content: r = ssh.disconnect(sessionOrRoute)
---
<a id="sshdisconnect"></a>

---
uid: Uplink2.Runtime.MiniScript.MiniScriptSshIntrinsics.SshExec
name: ssh.exec()
nameWithType: ssh.exec()
fullName: ssh.exec()
syntax:
  content: r = ssh.exec(sessionOrRoute, cmd, opts?)
---
<a id="sshexec"></a>

---
uid: Uplink2.Runtime.MiniScript.MiniScriptSshIntrinsics.SshInspect
name: ssh.inspect()
nameWithType: ssh.inspect()
fullName: ssh.inspect()
syntax:
  content: r = ssh.inspect(hostOrIp, userId, port=22, opts?)
---
<a id="sshinspect"></a>

---
uid: Uplink2.Runtime.MiniScript.MiniScriptFtpIntrinsics.FtpGet
name: ftp.get()
nameWithType: ftp.get()
fullName: ftp.get()
syntax:
  content: r = ftp.get(sessionOrRoute, remotePath, localPath?, opts?)
---
<a id="ftpget"></a>

---
uid: Uplink2.Runtime.MiniScript.MiniScriptFtpIntrinsics.FtpPut
name: ftp.put()
nameWithType: ftp.put()
fullName: ftp.put()
syntax:
  content: r = ftp.put(sessionOrRoute, localPath, remotePath?, opts?)
---
<a id="ftpput"></a>

---
uid: Uplink2.Runtime.MiniScript.MiniScriptNetIntrinsics.NetInterfaces
name: net.interfaces()
nameWithType: net.interfaces()
fullName: net.interfaces()
syntax:
  content: r = net.interfaces([sessionOrRoute])
---
<a id="netinterfaces"></a>

---
uid: Uplink2.Runtime.MiniScript.MiniScriptNetIntrinsics.NetScan
name: net.scan()
nameWithType: net.scan()
fullName: net.scan()
syntax:
  content: r = net.scan([sessionOrRoute], netId=null)
---
<a id="netscan"></a>

---
uid: Uplink2.Runtime.MiniScript.MiniScriptNetIntrinsics.NetPorts
name: net.ports()
nameWithType: net.ports()
fullName: net.ports()
syntax:
  content: r = net.ports([sessionOrRoute], hostOrIp, opts?)
---
<a id="netports"></a>

---
uid: Uplink2.Runtime.MiniScript.MiniScriptNetIntrinsics.NetBanner
name: net.banner()
nameWithType: net.banner()
fullName: net.banner()
syntax:
  content: r = net.banner([sessionOrRoute], hostOrIp, port)
---
<a id="netbanner"></a>

---
uid: Uplink2.Runtime.MiniScript.MiniScriptTermIntrinsics.TermExec
name: term.exec()
nameWithType: term.exec()
fullName: term.exec()
syntax:
  content: r = term.exec(cmd, opts?)
---
<a id="termexec"></a>

---
uid: Uplink2.Runtime.MiniScript.MiniScriptTermIntrinsics.TermPrint
name: term.print()
nameWithType: term.print()
fullName: term.print()
syntax:
  content: r = term.print(text)
---
<a id="termprint"></a>

---
uid: Uplink2.Runtime.MiniScript.MiniScriptTermIntrinsics.TermWarn
name: term.warn()
nameWithType: term.warn()
fullName: term.warn()
syntax:
  content: r = term.warn(text)
---
<a id="termwarn"></a>

---
uid: Uplink2.Runtime.MiniScript.MiniScriptTermIntrinsics.TermError
name: term.error()
nameWithType: term.error()
fullName: term.error()
syntax:
  content: r = term.error(text)
---
<a id="termerror"></a>

---
uid: Uplink2.Runtime.MiniScript.MiniScriptFsIntrinsics
name: Module fs
nameWithType: Module fs
fullName: Module fs
summary: <code>fs</code> 모듈은 가상 파일 시스템 조회/읽기/쓰기/삭제/메타 조회 API를 제공합니다.
---

---
uid: Uplink2.Runtime.MiniScript.MiniScriptFsIntrinsics.RegisterFsListIntrinsic
name: fs.list()
nameWithType: fs.list()
fullName: fs.list()
syntax:
  content: r = fs.list([sessionOrRoute], path)
---
<a id="fslist"></a>

---
uid: Uplink2.Runtime.MiniScript.MiniScriptFsIntrinsics.RegisterFsReadIntrinsic
name: fs.read()
nameWithType: fs.read()
fullName: fs.read()
syntax:
  content: r = fs.read([sessionOrRoute], path, opts?)
---
<a id="fsread"></a>

---
uid: Uplink2.Runtime.MiniScript.MiniScriptFsIntrinsics.RegisterFsWriteIntrinsic
name: fs.write()
nameWithType: fs.write()
fullName: fs.write()
syntax:
  content: r = fs.write([sessionOrRoute], path, text, opts?)
---
<a id="fswrite"></a>

---
uid: Uplink2.Runtime.MiniScript.MiniScriptFsIntrinsics.RegisterFsDeleteIntrinsic
name: fs.delete()
nameWithType: fs.delete()
fullName: fs.delete()
syntax:
  content: r = fs.delete([sessionOrRoute], path)
---
<a id="fsdelete"></a>

---
uid: Uplink2.Runtime.MiniScript.MiniScriptFsIntrinsics.RegisterFsStatIntrinsic
name: fs.stat()
nameWithType: fs.stat()
fullName: fs.stat()
syntax:
  content: r = fs.stat([sessionOrRoute], path)
---
<a id="fsstat"></a>

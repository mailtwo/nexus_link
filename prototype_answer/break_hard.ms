extract_pw = function(stdoutText)
  lines = stdoutText.split("\n")
  if len(lines) > 1 then
    pw = lines[1]
    if pw != null and pw != "" and pw != "ERROR" then
      return pw
    end if
  end if
  return null
end function

break_pw_5 = function(target)
  cmd = "ms password_breaker_hard.ms 5 " + target
  r = term.exec(cmd)
  pw = extract_pw(r.stdout)
  if pw != null then
    return pw
  end if
  return null
end function

is_guard_route = function(endpoint)
  s = net.scan(endpoint, "hardSubnet")
  if s == null or s.ok != 1 then
    return false
  end if

  // hostname 판별은 scan 커맨드 출력 사용
  scanText = ssh.exec(endpoint, "scan hardSubnet")
  if scanText != null and scanText.ok == 1 and scanText.stdout.indexOf("hard_mainframe") != null then
    return true
  end if

  return false
end function

guardRoutes = []
guardByIp = {}
visited = {}

dfs = function(endpoint, currentIp)
  if len(guardRoutes) >= 4 then
    return
  end if

  visited[currentIp] = 1

  if is_guard_route(endpoint) and not hasIndex(guardByIp, currentIp) then
    guardByIp[currentIp] = 1
    guardRoutes.push(endpoint)
    print "guard found: " + currentIp + " (" + str(len(guardRoutes)) + "/4)"
    if len(guardRoutes) >= 4 then
      return
    end if
  end if

  scanRes = net.scan(endpoint, "hardSubnet")
  if scanRes == null or scanRes.ok != 1 then
    return
  end if

  for nextIp in scanRes.ips
    if not hasIndex(visited, nextIp) then
      nextPw = break_pw_5(nextIp)

      if nextPw == null then
        visited[nextIp] = 1
      else
        opts = {}
        opts["session"] = endpoint
        c = ssh.connect(nextIp, "root", nextPw, opts)

        if c == null or c.ok != 1 then
          visited[nextIp] = 1
        else
          nextEndpoint = c.route
          if nextEndpoint == null then
            nextEndpoint = c.session
          end if

          dfs(nextEndpoint, nextIp)

          if len(guardRoutes) >= 4 then
            return
          end if
        end if
      end if
    end if
  end for
end function

main = function()
  // 1) hard_gateway password
  gatewayPw = term.exec("password_breaker 5 hard_gateway").stdout.split("\n")[1]
  //gatewayPw = "^2247"
  print "gatewayPw=" + gatewayPw

  // hard_gateway login
  gw = ssh.connect("hard_gateway", "root", gatewayPw)
  if gw == null or gw.ok != 1 then
    print "failed: connect hard_gateway"
    return
  end if

  // 2) net.scan(session, "hardSubnet")
  firstScan = net.scan(gw.session, "hardSubnet")
  if firstScan == null or firstScan.ok != 1 then
    print "failed: first net.scan"
    return
  end if

  print "hardSubnet neighbors from gateway:"
  for ip in firstScan.ips
    print ip
  end for

  // DFS 시작점 IP
  gatewayIp = "hard_gateway"
  if len(firstScan.interfaces) > 0 then
    gatewayIp = firstScan.interfaces[0].localIp
  end if

  // 3) DFS 순회 + 루프 방지 + 4개 guard route 수집
  dfs(gw.session, gatewayIp)

  if len(guardRoutes) < 4 then
    print "failed: guard route count=" + str(len(guardRoutes))
    return
  end if

  print "guard route count=4"

  // 6) 각 guard route에 fastconnect.ms 업로드
  i = 0
  while i < len(guardRoutes)
    r = ftp.put(guardRoutes[i], "/fastconnect.ms", "/")
    print "put fastconnect[" + str(i) + "] ok=" + str(r.ok) + " code=" + r.code
    i = i + 1
  end while

  // 7) 각 guard route에 /opt/bin/ms 업로드
  i = 0
  while i < len(guardRoutes)
    r = ftp.put(guardRoutes[i], "/opt/bin/ms", "/")
    print "put ms[" + str(i) + "] ok=" + str(r.ok) + " code=" + r.code
    i = i + 1
  end while

  print ssh.exec(guardRoutes[0], "ls").stdout

  // 8) guardRoute1에 /opt/bin/password_breaker 업로드
  rpb = ftp.put(guardRoutes[0], "/password_breaker_hard.ms", "/")
  print "put password_breaker[0] ok=" + str(rpb.ok) + " code=" + rpb.code

  // 9) 각 guard route에서 fastconnect 실행
  i = 0
  while i < len(guardRoutes)
    ex = ssh.exec(guardRoutes[i], "ms fastconnect.ms hard_mainframe root 1")
    print "exec fastconnect[" + str(i) + "] ok=" + str(ex.ok) + " code=" + ex.code
    i = i + 1
  end while

  // 10) guardRoute1에서 hard_mainframe password 획득
  mfOut = ssh.exec(guardRoutes[0], "ms password_breaker_hard.ms 5 hard_mainframe")
  hardMainframePasswd = mfOut.stdout.split("\n")[1]
  print "hardMainframePasswd=" + hardMainframePasswd

  // 11) guardRoute1 경유로 hard_mainframe 로그인
  optsMain = {}
  optsMain["session"] = guardRoutes[0]
  mainframeConn = ssh.connect("hard_mainframe", "root", hardMainframePasswd, optsMain)

  if mainframeConn == null then
    print "mainframe connect failed: null"
    return
  end if

  print "mainframe connect ok=" + str(mainframeConn.ok)
end function

main()

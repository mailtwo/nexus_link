// Aegis Shield SSH Stress Test / Connection Flood Tester
// Created: 2026-01-13
// Author: Pole Mario
// Target System: hard_mainframe
// Purpose: Verify Aegis Shield’s SSH connection defense (blocking effectiveness)

guard2_ssh = ssh.connect("hard_guard2", "root", "black_security_pw2")
if guard2_ssh == null or guard2_ssh.ok != 1 then
    print "failed: connect hard_guard2"
    return
end if

guard3_ssh = ssh.connect("hard_guard3", "root", "black_security_pw3")
if guard3_ssh == null or guard3_ssh.ok != 1 then
    ssh.disconnect(guard2_ssh.session)
    print "failed: connect hard_guard3"
    return
end if

ex = term.exec("password_breaker hard_guard4 root")
if ex == null or ex.ok == 0 then
    ssh.disconnect(guard2_ssh.session)
    ssh.disconnect(guard3_ssh.session)
    print "failed: password_breaker run error"
    return
end if

attack_result = ex.stdout.split("\n")[1]
if attack_result == "ERROR" then
    ssh.disconnect(guard2_ssh.session)
    ssh.disconnect(guard3_ssh.session)
    print "failed: cannot find guard4 password"
    return
end if

guard4_pw = attack_result
guard4_ssh = ssh.connect("hard_guard4", "root", guard4_pw)
if guard4_ssh == null or guard4_ssh.ok != 1 then
    ssh.disconnect(guard2_ssh.session)
    ssh.disconnect(guard3_ssh.session)
    print "failed: connect hard_guard4"
    return
end if

r = ftp.put(guard4_ssh, "/connection_test.ms", "/")
print "put guard4_ssh ok=" + str(r.ok) + " code=" + r.code

opts = {}
opts["async"] = 1
ex = term.exec("ms connection_test.ms", opts)
print "exec connection_test [guard1_ssh] ok=" + str(ex.ok) + " code=" + ex.code

ex = ssh.exec(guard2_ssh, "ms connection_test.ms", opts)
print "exec connection_test [guard2_ssh] ok=" + str(ex.ok) + " code=" + ex.code

ex = ssh.exec(guard3_ssh, "ms connection_test.ms", opts)
print "exec connection_test [guard3_ssh] ok=" + str(ex.ok) + " code=" + ex.code

ex = ssh.exec(guard4_ssh, "ms connection_test.ms", opts)
print "exec connection_test [guard4_ssh] ok=" + str(ex.ok) + " code=" + ex.code

ex = term.exec("password_breaker hard_mainframe root")
print "exec password_breaker [guard1_ssh] ok=" + str(ex.ok) + " code=" + ex.code
if ex == null or ex.ok != 1 then
    print "password_breaker for mainframe failed"
    return
end if

mainframe_pw = ex.stdout.split("\n")[1]
mainframe_ssh = ssh.connect("hard_mainframe", "root", mainframe_pw)
if mainframe_ssh != null and mainframe_ssh.ok == 1 then
    ssh.disconnect(mainframe_ssh.session)
end if

ssh.disconnect(guard2_ssh.session)
ssh.disconnect(guard3_ssh.session)
ssh.disconnect(guard4_ssh.session)
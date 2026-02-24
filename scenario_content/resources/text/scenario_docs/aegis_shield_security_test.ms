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

opts = {}
opts["async"] = 1
ex = term.exec("ms connection_test.ms", opts)
print "exec connection_test [guard1_ssh] ok=" + str(ex.ok) + " code=" + ex.code

ex = ssh.exec(guard2_ssh.session, "ms connection_test.ms", opts)
print "exec connection_test [guard2_ssh] ok=" + str(ex.ok) + " code=" + ex.code

ex = ssh.exec(guard3_ssh.session, "ms connection_test.ms", opts)
print "exec connection_test [guard3_ssh] ok=" + str(ex.ok) + " code=" + ex.code

ex = term.exec("password_breaker 4 hard_mainframe root")
print "exec password_breaker [guard1_ssh] ok=" + str(ex.ok) + " code=" + ex.code
if ex != null and ex.ok == 1 then
    lines = ex.stdout.split("\n")
    attack_result = ""
    if len(lines) > 1 then
        attack_result = lines[1]
    else if len(lines) == 1 then
        attack_result = lines[0]
    end if
    print attack_result
end if

ssh.disconnect(guard2_ssh.session)
ssh.disconnect(guard3_ssh.session)

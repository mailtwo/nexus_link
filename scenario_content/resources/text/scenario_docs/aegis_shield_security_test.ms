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
print "failed: connect hard_guard3"
return
end if\

opts = {}
opts["async"] = true
ex = term.exec("ms connection_test.ms", opts)
print "exec connection_test [guard1_ssh] ok=" + str(ex.ok) + " code=" + ex.code

ex = ssh.exec(guard2_ssh, "ms connection_test.ms", opts)
print "exec connection_test [guard2_ssh] ok=" + str(ex.ok) + " code=" + ex.code

ex = ssh.exec(guard3_ssh, "ms connection_test.ms", opts)
print "exec connection_test [guard3_ssh] ok=" + str(ex.ok) + " code=" + ex.code

ex = term.exec("password_breaker hard_mainframe root")
print "exec password_breaker [guard1_ssh] ok=" + str(ex.ok) + " code=" + ex.code
attack_result = ex.stdout.split("\n")[1]
print attack_result
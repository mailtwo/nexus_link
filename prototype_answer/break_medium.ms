main = function()
  gateway_pw = term.exec("password_breaker 4 medium_gateway").stdout.split("\n")[1]
  print gateway_pw

  result = ssh.connect("medium_gateway", "root", gateway_pw)
  if result == null or result.ok != 1 then
    return false
  end if
  opts = {}
  opts["session"] = result.session
  con_result = ssh.connect("medium_otp", "root", "", opts)
  result = ssh.exec(con_result.route, "/opt/bin/optgen")
  otpText = result.stdout
  print otpText.split("\n")[1]
  ssh.disconnect(con_result.route)

  result = ssh.connect("medium_gateway", "root", gateway_pw)
  opts = {}
  opts["session"] = result.session
  con_result2 = ssh.connect("medium_server", "root", otpText.split("\n")[1], opts)
  print con_result2.ok
  ssh.disconnect(con_result2.route)
end function

main()
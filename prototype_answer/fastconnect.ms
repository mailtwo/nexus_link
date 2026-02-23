if argc < 3 then
  print "usage: ms fastconnect.ms <host> <user> <password>"
else
  i = 0
  while i < 5
    r = ssh.connect(argv[0], argv[1], argv[2])
    i = i + 1
  end while
end if

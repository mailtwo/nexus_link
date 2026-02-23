trySshConnect = function (ip, userId, passwd)
    result = ssh.connect(ip, userId, passwd)
    if result == null or result.ok != 1 then
        if result != null and hasIndex(result, "err") and result.err != null then
            if result.err.indexOf("daemon") != null then
                return "STOP"
            end if
        end if
        return "FAIL"
    end if

    // 성공 세션 정리(필요 없으면 이 블록은 제거 가능)
    if hasIndex(result, "session") and result.session != null then
        ssh.disconnect(result.session)
    end if

    return "SUCCESS"
end function

printEstimatedMaxTime = function (targetLen)
    if targetLen >= 9 then
        print "[예상] 9자리 이상부터는 예상이 불가능할 정도로 오래걸림"
    else if targetLen == 8 then
        print "[예상] 8자리 최대 약 290091.15초 (약 80.58시간, 약 3.36일)"
    else if targetLen == 7 then
        print "[예상] 7자리 최대 약 14504.56초 (약 4.03시간)"
    else if targetLen == 6 then
        print "[예상] 6자리 최대 약 725.23초 (약 12.09분)"
    else if targetLen == 5 then
        print "[예상] 5자리 최대 약 36.26초"
    else if targetLen == 4 then
        print "[예상] 4자리 최대 약 1.81초"
    else if targetLen == 3 then
        print "[예상] 3자리 최대 약 0.09초"
    else if targetLen == 2 then
        print "[예상] 2자리 최대 약 0.00초"
    else
        print "[예상] 1자리 최대 약 0.00초"
    end if
end function

if argc < 2 then
	print "ERROR"
else
	n = floor(val(argv[0]))
	target = argv[1]
    if argc >= 3 then
        userId = argv[2]
    else
        userId = "root"
    end if
	if n < 1 then
		print "ERROR"
	else
		alphabet = "0123456789)!@#$%^&*("
        printEstimatedMaxTime(n)

		emitAll = function (prefix, remaining, alphabet)
			if remaining == 0 then
				connectStatus = trySshConnect(target, userId, prefix)
                if connectStatus == "SUCCESS" then
                    return prefix
				end if
                if connectStatus == "STOP" then
                    return "__STOP__"
                end if
                return null
			end if

			i = 0
			while i < len(alphabet)
				result = emitAll(prefix + alphabet[i], remaining - 1, alphabet)
                if result != null then
                    return result
                end if
				i = i + 1
			end while
            return null
		end function

		length = 1
        result = null
		while length <= n
			result = emitAll("", length, alphabet)
            if result != null then
                if result == "__STOP__" then
                    break
                end if

                print result
                break
            end if
			length = length + 1
		end while

        if result == null or result == "__STOP__" then
            print "ERROR"
        end if
	end if
end if

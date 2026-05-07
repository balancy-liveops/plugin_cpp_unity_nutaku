const BalancyWebGL = {
    getUserLocal: function () {
        const loc = new Intl.Locale(navigator.language || navigator.userLanguage);

        const res = JSON.stringify({
            language: loc.language,
            region: loc.region
        });
        
        const bufferSize = lengthBytesUTF8(res) + 1;
        const buffer = _malloc(bufferSize);
        stringToUTF8(res, buffer, bufferSize);

        return buffer;
    },
    getRealUserId: function (req_id) {
        req_id = UTF8ToString(req_id);
        if (balancyData.user && req_id) {
            gameInstance.SendMessage('BalancyWebGLObject', 'OnUserId', JSON.stringify({
                req_id:   req_id,
                response: {
                    success: true,
                    data:    JSON.stringify({
                        id:       balancyData.user.id,
                        nickname: balancyData.user.nickname
                    })
                }
            }));

            return;
        }

        NutakuGI.getQuickUserInfo().then(result => {
            if (!result.error) {
                const id       = result.id;
                const nickname = result.nickname;

                balancyData.user = {
                    id:       id,
                    nickname: nickname
                };

                gameInstance.SendMessage('BalancyWebGLObject', 'OnUserId', JSON.stringify({
                    req_id:   req_id,
                    response: {
                        success: true,
                        data:    JSON.stringify({
                            id:       balancyData.user.id,
                            nickname: balancyData.user.nickname
                        })
                    }
                }));
            } else {
                gameInstance.SendMessage('BalancyWebGLObject', 'OnUserId', JSON.stringify({
                    req_id:   req_id,
                    response: {
                        success: false,
                        data:    result.error
                    }
                }));
            }
        });
    },
    handshake: function (req_id) {
        req_id = UTF8ToString(req_id);

        NutakuGI.startHandshake().then(handshakeResult => {
            if (handshakeResult.error) {
                gameInstance.SendMessage('BalancyWebGLObject', 'OnHandshake', JSON.stringify({
                    req_id:   req_id,
                    response: {
                        success: false,
                        data:    handshakeResult.error
                    }
                }));

                return;
            }

            switch (handshakeResult.game_rc) {
                case 0:
                case 500:
                case 400:
                    gameInstance.SendMessage('BalancyWebGLObject', 'OnHandshake', JSON.stringify({
                        req_id:   req_id,
                        response: {
                            success: false,
                            data:    'Handshake error'
                        }
                    }));

                    return;
            }

            NutakuGI.getQuickUserInfo().then(infoResult => {
                if (!infoResult.error) {
                    const id       = infoResult.id;
                    const nickname = infoResult.nickname;

                    balancyData.user = {
                        id:       id,
                        nickname: nickname
                    };

                    gameInstance.SendMessage('BalancyWebGLObject', 'OnHandshake', JSON.stringify({
                        req_id:   req_id,
                        response: {
                            success: true,
                            data:    JSON.stringify({
                                token: JSON.parse(handshakeResult.message).token,
                                id:    infoResult.id
                            })
                        }
                    }));
                } else {
                    gameInstance.SendMessage('BalancyWebGLObject', 'OnHandshake', JSON.stringify({
                        req_id:   req_id,
                        response: {
                            success: false,
                            data:    infoResult.error
                        }
                    }));
                }
            });

        });
    },
    purchase:  function (env, id, name, price, img, desc, req_id) {
        const itemId      = UTF8ToString(id);
        const itemName    = UTF8ToString(name);
        const imageUrl    = UTF8ToString(img);
        const description = UTF8ToString(desc);
        req_id            = UTF8ToString(req_id);

        NutakuGI.paymentObject.price       = price;
        NutakuGI.paymentObject.name        = itemName;
        NutakuGI.paymentObject.skuId       = itemId;
        NutakuGI.paymentObject.imgUrl      = imageUrl;
        NutakuGI.paymentObject.description = description;
        NutakuGI.paymentObject.message     = 'пипка';

        NutakuGI.createPayment(NutakuGI.paymentObject).then(result => {
            if (result === undefined) {
                gameInstance.SendMessage('BalancyWebGLObject', 'OnPurchase', JSON.stringify({
                    req_id:   req_id,
                    response: {
                        success: false,
                        data:    'Fields could not be empty'
                    }
                }));

                return;
            }
            if (result.error) {
                gameInstance.SendMessage('BalancyWebGLObject', 'OnPurchase', JSON.stringify({
                    req_id:   req_id,
                    response: {
                        success: false,
                        data:    result.error
                    }
                }));
            } else {
                let payment = result;
                switch (payment.status) {
                    case 'success':
                        gameInstance.SendMessage('BalancyWebGLObject', 'OnPurchase', JSON.stringify({
                            req_id:   req_id,
                            response: {
                                success: true,
                                data:    JSON.stringify({payment_id: payment.paymentId})
                            }
                        }));
                        break;
                    default:
                        gameInstance.SendMessage('BalancyWebGLObject', 'OnPurchase', JSON.stringify({
                            req_id:   req_id,
                            response: {
                                success: false,
                                data:    payment.status
                            }
                        }));
                        break;
                }
            }
        });
    }
}

mergeInto(LibraryManager.library, BalancyWebGL);

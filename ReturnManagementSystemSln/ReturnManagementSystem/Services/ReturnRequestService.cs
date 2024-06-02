﻿using ReturnManagementSystem.Exceptions;
using ReturnManagementSystem.Interfaces;
using ReturnManagementSystem.Models;
using ReturnManagementSystem.Models.DTOs.ProductDTOs;
using ReturnManagementSystem.Models.DTOs.RRandPaymentDTOs;
using ReturnManagementSystem.Repositories;

namespace ReturnManagementSystem.Services
{
    public class ReturnRequestService : IReturnRequestService
    {
        private readonly IRepository<int,ReturnRequest> _returnRequestRepository;
        private readonly IRepository<int,Order> _orderRepository;
        private readonly IRepository<int,OrderProduct> _orderProductRepository;
        private readonly IRepository<int,Product> _productRepository;
        private readonly IRepository<int,ProductItem> _productItemRepository;
        private readonly IRepository<int,Policy> _policyRepository;
        private readonly IProductItemService _productItemService;
        private readonly IPaymentService _paymentService;

        public ReturnRequestService(IRepository<int, ReturnRequest> returnRequestRepository,
            IRepository<int, Order> orderRepository,
            IRepository<int, OrderProduct> orderProductRepository,
            IRepository<int, Product> productRepository,
            IRepository<int, ProductItem> productItemRepository,
            IRepository<int, Policy> policyRepository,
            IProductItemService productItemService,
            IPaymentService paymentService
            )
        {
            _returnRequestRepository = returnRequestRepository;
            _orderProductRepository = orderProductRepository;
            _orderRepository = orderRepository;
            _productRepository = productRepository;
            _productItemRepository = productItemRepository;
            _policyRepository = policyRepository;
            _productItemService = productItemService;
            _paymentService = paymentService;
        }
        public async Task<ReturnRequest> CloseReturnRequest(int userId , CloseRequestDTO crrDTO)
        {
            var returnRequest = await _returnRequestRepository.Get(crrDTO.RequestId);
            if (returnRequest == null)
            {
                throw new ObjectNotFoundException("Return request not found");
            }

            returnRequest.Feedback = crrDTO.Feedback;
            returnRequest.Status = "Closed";
            returnRequest.ClosedDate = DateTime.UtcNow;
            returnRequest.ClosedBy = userId;

            return await _returnRequestRepository.Update(returnRequest);
        }

        public async Task<ReturnRequest> OpenReturnRequest(ReturnRequestDTO returnRequestDTO)
        {
            var order = await _orderRepository.Get(returnRequestDTO.OrderId);
            if( order == null || order.UserId != returnRequestDTO.UserId || order.OrderStatus != "Delivered")
            {
                throw new ObjectNotFoundException("Invalid Order");
            }
            var opress = order.OrderProducts.Where(op => op.ProductId == returnRequestDTO.ProductId).FirstOrDefault();
            if(opress == null)
            {
                throw new ObjectNotFoundException("Product Not Found in the Order");
            }
            var policyress = await _policyRepository.FindAll(pr => pr.ProductId == returnRequestDTO.ProductId && pr.PolicyType == returnRequestDTO.ReturnPolicy);
            if(policyress == null)
            {
                throw new InvalidReturnRequest("Specified Return Policy is not Applicable for this Product");
            }
            var policyres = policyress.FirstOrDefault();
            var orderedDate = order.OrderDate;
            var duration = (DateTime.Now - orderedDate).Value.TotalDays;
            if(duration > policyres.Duration)
            {
                throw new InvalidReturnRequest("The Return Policy Duration has been Exceeded");
            }
            var returnRequest = new ReturnRequest
            {
                UserId = returnRequestDTO.UserId,
                OrderId = returnRequestDTO.OrderId,
                ProductId = returnRequestDTO.ProductId,
                ReturnPolicy = returnRequestDTO.ReturnPolicy,
                Reason = returnRequestDTO.Reason,
                RequestDate = DateTime.Now,
                Status = "Pending"
            };
            return await _returnRequestRepository.Add(returnRequest);
        }

        public async Task<ReturnRequest> TechnicalReview(TechnicalReviewDTO technicalReviewDTO)
        {
            var returnRequest = await _returnRequestRepository.Get(technicalReviewDTO.requestId);
            if (returnRequest == null)
            {
                throw new ObjectNotFoundException("Return request not found");
            }

            switch (technicalReviewDTO.process)
            {
                case "Return Good":
                    await HandleReturnGood(returnRequest);
                    break;
                case "Return Bad":
                    await HandleReturnBad(returnRequest);
                    break;
                case "Replace Repaired":
                    await HandleReplaceRepaired(returnRequest);
                    break;
                case "Replace Bad":
                    await HandleReplaceBad(returnRequest);
                    break;
                case "Repaired":
                    await HandleRepaired(returnRequest);
                    break;
                default:
                    throw new InvalidDataException("Invalid Process");
            }

            returnRequest.Process = technicalReviewDTO.process;
            returnRequest.Feedback = technicalReviewDTO.feedback;

            return await _returnRequestRepository.Update(returnRequest);
        }

        private async Task HandleReturnGood(ReturnRequest returnRequest)
        {
            await ProcessRefundTransaction(returnRequest);
            var upis = new UpdateProductItemStatus()
            {
                SerialNumber = returnRequest.SerialNumber,
                Status = "Available"
            };
            await _productItemService.UpdateProductItemStatus(upis);
        }

        private async Task HandleReturnBad(ReturnRequest returnRequest)
        {
            await ProcessRefundTransaction(returnRequest);
            var upis = new UpdateProductItemStatus()
            {
                SerialNumber = returnRequest.SerialNumber,
                Status = "Disposed"
            };
            await _productItemService.UpdateProductItemStatus(upis);
        }

        private async Task HandleReplaceRepaired(ReturnRequest returnRequest)
        {
            var availableItems = await _productItemRepository.FindAll(pi => pi.ProductId == returnRequest.ProductId && pi.Status == "Available");
            if (availableItems == null)
            {
                throw new InvalidDataException("Product Out of Stock for Replacement");
            }

            var replacementItem = availableItems.First();
            replacementItem.Status = "Replaced";
            await _productItemRepository.Update(replacementItem);

            var orderProduct = await _orderProductRepository.FindAll(op => op.OrderId == returnRequest.OrderId && op.ProductId == returnRequest.ProductId && op.SerialNumber == returnRequest.SerialNumber);
            var orderProductToUpdate = orderProduct.First();
            orderProductToUpdate.SerialNumber = replacementItem.SerialNumber;
            await _orderProductRepository.Update(orderProductToUpdate);

            var upis = new UpdateProductItemStatus()
            {
                SerialNumber = returnRequest.SerialNumber,
                Status = "Disposed"
            };
            await _productItemService.UpdateProductItemStatus(upis);
        }

        private async Task HandleReplaceBad(ReturnRequest returnRequest)
        {
            await HandleReplaceRepaired(returnRequest);
            var upis = new UpdateProductItemStatus()
            {
                SerialNumber = returnRequest.SerialNumber,
                Status = "Disposed"
            };
            await _productItemService.UpdateProductItemStatus(upis);
        }

        private async Task HandleRepaired(ReturnRequest returnRequest)
        {
            var upis = new UpdateProductItemStatus()
            {
                SerialNumber = returnRequest.SerialNumber,
                Status = "Repaired"
            };
            await _productItemService.UpdateProductItemStatus(upis);
        }

        private async Task ProcessRefundTransaction(ReturnRequest returnRequest)
        {
            var order = returnRequest.Order;
            //var orderproduct = order.OrderProducts.Where(op => op.SerialNumber == returnRequest.SerialNumber);
            var orderproduct = (await _orderProductRepository.FindAll(op=>op.SerialNumber == returnRequest.SerialNumber)).First();
            TransactionDTO transaction = new TransactionDTO()
            {
                RequestId = returnRequest.RequestId,
                TransactionType = "Refund",
                TransactionId = Guid.NewGuid().ToString(),
                PaymentDate = DateTime.Now,
                Amount = (decimal)orderproduct.Price
            };
            await _paymentService.ProcessPayment(transaction);
        }

        public async Task<ReturnRequest> UpdateUserSerialNumber(UpdateRequestSerialNumberDTO ursnDTO)
        {
            var returnRequest = await _returnRequestRepository.Get(ursnDTO.RquestId);
            if (returnRequest == null)
            {
                throw new ObjectNotFoundException("Return request not found");
            }
            var sn = await _orderProductRepository.FindAll(op=>op.OrderId == returnRequest.OrderId && op.SerialNumber == ursnDTO.SerialNumber);
            if(sn == null)
            {
                throw new InvalidSerialNumber("Invalid Serial Number");
            }
            returnRequest.SerialNumber = ursnDTO.SerialNumber;
            returnRequest.Status = "Processing";

            return await _returnRequestRepository.Update(returnRequest);
        }

        public async Task<IEnumerable<ReturnRequest>> GetAllReturnRequests()
        {
            var returnrequests = await _returnRequestRepository.FindAllWithIncludes(rr => rr.Status != "Closed", r=>r.Transactions, r=>r.Product, r=>r.Order);
            if (returnrequests == null)
                throw new ObjectsNotFoundException("Return Requests Not Found");
            return returnrequests;
        }

        public async Task<IEnumerable<ReturnRequest>> GetAllUserReturnRequests(int userid)
        {
            var returnrequests = await _returnRequestRepository.FindAllWithIncludes(rr => rr.UserId == userid, r => r.Transactions, r => r.Product, r => r.Order);
            if (returnrequests == null)
                throw new ObjectsNotFoundException("Return Requests Not Found");
            return returnrequests;
        }

        public async Task<ReturnRequest> GetReturnRequest(int requestid)
        {
            var returnrequest = await _returnRequestRepository.FindAllWithIncludes(rr => rr.RequestId == requestid, r => r.Transactions, r => r.Product, r => r.Order);
            if (returnrequest == null)
                throw new ObjectNotFoundException("Return Request Not Found");
            return returnrequest.FirstOrDefault();
        }
    }
}
